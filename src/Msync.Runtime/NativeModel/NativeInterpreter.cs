using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter : IAsyncDisposable
{
    private Generation generation;
    private readonly AsyncLocal<Generation?> activeGeneration = new();
    private NativeImage image => (activeGeneration.Value ?? generation).Image;
    private NativeEnvironmentLayout[] environments => (activeGeneration.Value ?? generation).Environments;
    private NativeEnvironmentLayout?[] explicitEnvironments => (activeGeneration.Value ?? generation).ExplicitEnvironments;
    private readonly RuntimeProfile? profile;
    private readonly NativeRuntimeContext sessionContext;
    private readonly AsyncLocal<NativeRuntimeContext?> deferredContext = new();
    private NativeRuntimeContext context => deferredContext.Value ?? sessionContext;
    private readonly NativeMemory memory = new();
    private readonly AsyncLocal<NativeWorkScope?> currentWork = new();
    private readonly NativeWorkScope executionWork = new(null);
    private StaticState[] statics;
    private readonly Dictionary<int, NativeAddress> sessionLocations = [];
    private readonly Dictionary<(int OriginId, int OriginOffset), NativeValue> retainedLiterals = [];
    private readonly ConditionalWeakTable<NativeRange, RangeRoots> rangeRoots = new();
    private readonly ConditionalWeakTable<NativeRange, DatumRange> datumRanges = new();
    private readonly SemaphoreSlim transferSlots;
    private int failedTransfers;
    private bool disposed;

    public NativeInterpreter(NativeProgram program, ExecutionOptions? options = null,
        NativeRuntimeContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        sessionContext = context ?? new(options, program.Context);
        profile = options?.Profile;
        tierOptions = options?.Tiers ?? NativeTierOptions.FromEnvironment();
        tierOptions.Validate();
        NativeHostIO.Attach(this.context);
        Action<NativeProcess>? created = this.context.ProcessCreated;
        this.context.ProcessCreated = process =>
        {
            created?.Invoke(process);
            (currentWork.Value ?? executionWork).Add(process);
        };
        this.context.DescribeFunction = DescribeFunction;
        generation = new(new(program, this.context, retainedLiterals));
        statics = Enumerable.Range(0, program.Statics.Count).Select(_ => new StaticState()).ToArray();
        int concurrency = Math.Max(1, Math.Min(this.context.Jobs,
            (int)Math.Min(int.MaxValue, this.context.MemoryLimit / Math.Max(1, this.context.BufferSize))));
        transferSlots = new(concurrency);
    }

    /// <summary>Publishes a session image without reanchoring existing storage or suspended frames.</summary>
    public void UpdateProgram(NativeProgram program)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(program);
        NativeImage next = new(program, context, retainedLiterals);
        if (program.Statics.Count < statics.Length)
            throw new MsyncException("A native session cannot remove static declaration identities.");
        if (program.Statics.Count != statics.Length)
        {
            StaticState[] expanded = new StaticState[program.Statics.Count];
            statics.CopyTo(expanded, 0);
            for (int index = statics.Length; index < expanded.Length; index++) expanded[index] = new();
            statics = expanded;
        }
        generation = new(next);
    }

    /// <summary>Tests canonical entry storage, including a pending typed result.</summary>
    public bool IsLocalInitialized(int slot) => sessionLocations.TryGetValue(slot, out NativeAddress address)
        && memory.TryRead(address, out _);

    /// <summary>Projects a ready entry value without demanding or observing pending work.</summary>
    public NativeValue? GetLocalValue(int slot)
    {
        if (!sessionLocations.TryGetValue(slot, out NativeAddress address) || !memory.TryRead(address, out NativeDatum datum)) return null;
        if (datum.IsReady) return datum.Value;
        return datum.Future!.Completion.IsCompletedSuccessfully ? datum.Future.Completion.Result : (NativeValue?)null;
    }

    public int FailedTransfers => Volatile.Read(ref failedTransfers);

    public async ValueTask<NativeValue> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeWorkScope? previous = currentWork.Value;
        currentWork.Value = executionWork;
        try
        {
            NativeDatum result = await RunFunctionAsync(image.Program.EntryPoint, null, [], cancellationToken);
            return result.Future is { } future ? await future.CompleteAsync(cancellationToken) : result.Value;
        }
        finally
        {
            executionWork.CountCompletedTransfers(() => Interlocked.Increment(ref failedTransfers));
            currentWork.Value = previous;
        }
    }

    public async ValueTask<NativeValue> InvokeAsync(NativeValue callable,
        IReadOnlyList<NativeValue> arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateHostPointerArguments(callable, arguments);
        NativeDatum result = await InvokeDatumAsync(callable,
            arguments.Select(static value => new NativeDatum(value)).ToArray(), cancellationToken);
        return await result.ResolveAsync(cancellationToken);
    }

    private async ValueTask<NativeDatum> InvokeDatumAsync(NativeValue callable,
        IReadOnlyList<NativeDatum> arguments, CancellationToken token)
    {
        NativeFunction function = callable.AsFunction();
        if (function.NativeName is { } name)
            return await InvokeNativeDatumAsync(name, arguments, context.CaptureDirectory(), null, token);
        return await RunFunctionAsync(function.Function, function.Environment, arguments, token);
    }

    private async ValueTask<NativeDatum> RunFunctionAsync(int index, NativeEnvironment? environment,
        IReadOnlyList<NativeDatum> arguments, CancellationToken token)
    {
        Generation? previousGeneration = activeGeneration.Value;
        Generation selected = previousGeneration ?? generation;
        NativeWorkScope? parent = currentWork.Value;
        List<TailReturn>? returns = null;
        TailInvocation? origin = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                NativeFunctionCode function = selected.Image.Program.Functions[index];
                if (function.IsTemplate) throw new MsyncException("The selected callable requires a compiled specialization.");
                if (arguments.Count != function.ParameterCount)
                    throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {arguments.Count}.");
                NativeWorkScope owner = new(parent ?? executionWork)
                { Counters = profile?.EnterNative(selected.Image.Program, index) ?? default };
                NativeFrame frame = new(selected.Image, index, environment, arguments, memory,
                    index == selected.Image.Program.EntryPoint && selected.Image.Program.PreserveLocals ? sessionLocations : null);
                Activation activation = new(index, function, frame, owner, ExecutionFunction(selected.Image, index));
                NativeDatum result = new(NativeValue.Null);
                activeGeneration.Value = selected;
                currentWork.Value = owner;
                try { result = await RunActivationAsync(activation, token); }
                finally
                {
                    try { await FinishActivationAsync(activation, environment, arguments, result, token); }
                    finally { currentWork.Value = parent; activeGeneration.Value = previousGeneration; }
                }
                if (activation.Tail is not { } tail)
                {
                    if (returns is not null)
                        for (int pending = returns.Count - 1; pending >= 0; pending--) returns[pending].Complete();
                    return result;
                }
                if (profile is not null) (returns ??= []).Add(new(owner.Counters, function, tail.Pc, tail.Plan));
                origin = tail;
                index = tail.Target;
                environment = tail.Environment;
                arguments = tail.Arguments;
            }
        }
        catch (Exception error) when (origin is not null)
        {
            ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error);
            throw;
        }
    }

    private async ValueTask<NativeDatum> RunActivationAsync(Activation activation, CancellationToken token)
    {
        NativeFunctionCode function = activation.Function;
        NativeFrame frame = activation.Frame;
        NativeWorkScope owner = activation.Work;
        bool enableTiering = !disposed && (tierOptions.Policy == NativeJitPolicy.Force
            || tierOptions.Policy == NativeJitPolicy.Auto && NativeMachineCode.AutoEnabled);
        NativeExecutionFunction executable = activation.Execution;
        if (enableTiering) executable.Enter();
        NativeInstruction[] instructions = executable.Metadata;
        bool linkedCounters = tierOptions.Policy == NativeJitPolicy.Linked;
        bool observed = profile is not null;
        if (linkedCounters) { tierCounters.LinkedEntry(); owner.Counters.LinkedEntry(); }
        NativeCompiledFunction? version = null;
        bool promotionPoint = true;
        for (int pc = 0; pc < instructions.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (enableTiering && promotionPoint)
            {
                version = executable.TryCompile(tierCounters, owner.Counters);
                promotionPoint = false;
            }
            if (version is not null)
            {
                TierOutcome outcome = await RunTierAsync(activation, executable, version, pc, token);
                if (outcome.Returned) return activation.Result;
                pc = outcome.Next;
                if (!outcome.ExecuteBaseline) continue;
                version = null;
            }
            if (enableTiering) ObserveTier(executable, frame, pc);
            NativeStep step;
            if (observed) executable.Visit(owner.Counters, pc);
            if (linkedCounters) { tierCounters.LinkedOperation(); owner.Counters.LinkedOperation(); }
            try
            {
                ValueTask<NativeStep> pending = ExecuteLinked(activation, executable.Instructions[pc], pc, token);
                bool suspended = !pending.IsCompleted;
                if (suspended) owner.Counters.Pause();
                try { step = await pending; }
                finally { if (suspended) owner.Counters.Continue(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (MsyncException error) when (error.Span is null)
            {
                if (executable.Diagnostic(pc).Source is { } source)
                    throw new MsyncException(error.Message, source, error);
                throw;
            }
            catch (InvalidOperationException error)
            {
                NativeInstruction instruction = executable.Diagnostic(pc);
                throw new MsyncException($"{error.Message} Native {instruction.Op} in function '{function.Name}', instruction {pc}, operands {instruction.A}/{instruction.B}/{instruction.C}, result {instruction.Result}.", instruction.Source, error);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { throw new MsyncException(error.Message, executable.Diagnostic(pc).Source, error); }
            if (step.Returned) return activation.Result;
            if (observed) owner.Counters.Edge(pc + executable.Instructions[pc].Span - 1, step.Next);
            if (step.Next <= pc) { if (enableTiering) executable.Backedge(); promotionPoint = true; }
            pc = step.Next;
        }
        return new(NativeValue.Null);
    }

    private async ValueTask FinishActivationAsync(Activation activation, NativeEnvironment? environment,
        IReadOnlyList<NativeDatum> arguments, NativeDatum result, CancellationToken token)
    {
        Exception? iteratorFailure = null;
        try { await activation.CloseIteratorsAsync(); }
        catch (Exception error) { iteratorFailure = error; }
        if (activation.Work.HasWork)
        {
            NativeReachable reachable = new(memory, GetRangeRoots, image);
            reachable.Add(result);
            foreach (NativeDatum argument in arguments) reachable.Add(argument);
            foreach (StaticState state in statics)
                if (Volatile.Read(ref state.Allocation) is { } allocation)
                    reachable.Add(new NativeDatum(NativeValue.FromAddress(allocation.Address)));
            if (environment is not null)
                for (int capture = 0; capture < environment.Count; capture++)
                {
                    if (environment.Layout.Captures[capture].Location) reachable.AddLocation(environment[capture].Location);
                    else reachable.AddLocation(environment.CaptureAddress(capture));
                }
            if (activation.FunctionIndex == image.Program.EntryPoint && image.Program.SessionRoots is { } roots)
                foreach (int root in roots) reachable.AddLocation(activation.Frame.Location(root));
            try { await activation.Work.FinishAsync(reachable, () => Interlocked.Increment(ref failedTransfers), token); }
            catch (Exception error) when (iteratorFailure is not null)
            { throw new AggregateException("Native iterator and work cleanup failed.", iteratorFailure, error); }
        }
        if (iteratorFailure is not null) ExceptionDispatchInfo.Capture(iteratorFailure).Throw();
    }



    private NativeAddress EnvironmentAddress(NativeFunction function, NativeInstruction instruction)
    {
        NativeAddress address = function.Environment?.Address
            ?? throw new MsyncException("The selected function has no capture environment.", instruction.Source);
        NativeStorage.ValidateRegion(address, image.Layout(instruction.B), 1);
        return address;
    }

    private async ValueTask<NativeStep> RangeInstructionAsync(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token)
    {
        TypeRef? logicalType = instruction.C >= 0 ? (await frame.GetDatum(instruction.C).ResolveAsync(token)).AsType() : null;
        if (instruction.Op == NativeOp.DeferRange) MakeDeferredRange(frame, instruction, logicalType);
        else MakeRange(frame, instruction, instruction.Op == NativeOp.MakeLazyRange, logicalType);
        return new(pc + 1);
    }



    private static int Count(NativeValue operand)
    {
        double value = operand.AsNumber();
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
            throw new MsyncException($"Native memory count/index must be a non-negative integer, received {value}.");
        return (int)value;
    }

    private static async ValueTask<NativeStep> BranchAsync(NativeDatum condition, int whenTrue, int whenFalse, CancellationToken token) =>
        new((await condition.ResolveAsync(token)).AsBool() ? whenTrue : whenFalse);

    private NativeCallableDescription? DescribeFunction(NativeFunction function) => image.DescribeFunction(function);

    private IReadOnlyList<NativeDatum>? GetRangeRoots(NativeRange range) => rangeRoots.TryGetValue(range, out RangeRoots? roots) ? roots.Values : null;
    private sealed record DatumRange(IAsyncEnumerable<NativeDatum> Items, bool MayDefer = true);
    private sealed record RangeRoots(IReadOnlyList<NativeDatum> Values);
    private readonly record struct NativeStep(int Next, bool Returned = false);

    private sealed class StaticState
    {
        internal NativeAllocation? Allocation;
        internal int Ready;
    }

    private sealed class Generation(NativeImage image)
    {
        internal NativeImage Image { get; } = image;
        internal NativeEnvironmentLayout[] Environments { get; } = image.Program.Functions.Select(function =>
            new NativeEnvironmentLayout(function.Signature.Captures.Select(capture => new NativeCaptureLayout(
                image.Type(capture.Type), capture.Mode == NativeCaptureMode.Location)))).ToArray();
        internal NativeEnvironmentLayout?[] ExplicitEnvironments { get; } = new NativeEnvironmentLayout?[image.Program.Layouts.Count];
        internal TailPlan?[]?[] TailSites { get; } = new TailPlan?[]?[image.Program.Functions.Count];
    }

    private sealed class Activation(int functionIndex, NativeFunctionCode function, NativeFrame frame, NativeWorkScope work, NativeExecutionFunction execution)
    {
        internal NativeExecutionFunction Execution { get; } = execution;
        internal int FunctionIndex { get; } = functionIndex;
        internal NativeFunctionCode Function { get; } = function;
        internal NativeFrame Frame { get; } = frame;
        internal NativeWorkScope Work { get; } = work;
        internal NativeDatum Result { get; set; }
        internal TailInvocation? Tail { get; set; }
        internal IAsyncEnumerator<NativeDatum>?[] Iterators { get; } = function.IteratorCount == 0 ? [] : new IAsyncEnumerator<NativeDatum>?[function.IteratorCount];
        internal Dictionary<int, NativeDatum[]>? Plans;
        internal Dictionary<string, HashSet<NativeFuture>>? Regions;
        internal async ValueTask CloseIteratorsAsync()
        {
            List<Exception>? failures = null;
            for (int index = Iterators.Length - 1; index >= 0; index--)
            {
                IAsyncEnumerator<NativeDatum>? iterator = Iterators[index];
                Iterators[index] = null;
                if (iterator is null) continue;
                try { await iterator.DisposeAsync(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null) throw new AggregateException("Native iterator cleanup failed.", failures);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        executionWork.AbortProcesses();
        try { await executionWork.FinishAsync(new(memory, GetRangeRoots, image), () => Interlocked.Increment(ref failedTransfers), CancellationToken.None); }
        finally
        {
            foreach (KeyValuePair<NativeImage, NativeExecutionImage> entry in executableImages) entry.Value.Dispose();
            transferSlots.Dispose();
        }
    }
}
