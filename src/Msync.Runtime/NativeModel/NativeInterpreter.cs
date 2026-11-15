using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter : IAsyncDisposable
{
    private Generation generation;
    private volatile bool programUpdated;
    private readonly AsyncLocal<Generation?> activeGeneration = new();
    private NativeImage image => (programUpdated ? activeGeneration.Value ?? generation : generation).Image;
    private NativeEnvironmentLayout[] environments => (programUpdated ? activeGeneration.Value ?? generation : generation).Environments;
    private NativeEnvironmentLayout?[] explicitEnvironments => (programUpdated ? activeGeneration.Value ?? generation : generation).ExplicitEnvironments;
    private readonly RuntimeProfile? profile;
    private readonly NativeRuntimeContext sessionContext;
    private readonly Action<NativeProcess>? processCreatedObserver;
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
        processCreatedObserver = created;
        this.context.ProcessCreated = process =>
        {
            created?.Invoke(process);
            (currentWork.Value ?? executionWork).Add(process);
        };
        this.context.DescribeFunction = DescribeFunction;
        if (program.Stage == ArtifactStage.Relocatable) program = NativeBackendCompiler.Compile(program, this.context);
        generation = new(new(program, this.context, retainedLiterals));
        sessionContext.Connections.Acquire();
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
        if (program.Stage == ArtifactStage.Relocatable) program = NativeBackendCompiler.Compile(program, context);
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
        programUpdated = true;
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

    public ValueTask<NativeValue> ExecuteAsync(CancellationToken cancellationToken = default) =>
        new(Task.Factory.StartNew(() => ExecuteEntry(cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default));

    private NativeValue ExecuteEntry(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeWorkScope? previous = currentWork.Value;
        Generation? previousGeneration = activeGeneration.Value;
        currentWork.Value = executionWork;
        activeGeneration.Value = generation;
        try
        {
            ValidateInvocation(image.Program.EntryPoint, 0);
            NativeDatum result = RunFunction(image.Program.EntryPoint, null, default, cancellationToken);
            return Demand(result.ResolveAsync(cancellationToken));
        }
        finally
        {
            executionWork.CountCompletedTransfers(() => Interlocked.Increment(ref failedTransfers));
            currentWork.Value = previous;
            activeGeneration.Value = previousGeneration;
        }
    }

    public ValueTask<NativeValue> InvokeAsync(NativeValue callable,
        IReadOnlyList<NativeValue> arguments, CancellationToken cancellationToken = default) =>
        currentWork.Value is not null ? InvokeValueAsync(callable, arguments, cancellationToken)
            : new(Task.Factory.StartNew(() => Demand(InvokeValueAsync(callable, arguments, cancellationToken)),
                cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    private async ValueTask<NativeValue> InvokeValueAsync(NativeValue callable,
        IReadOnlyList<NativeValue> arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateHostPointerArguments(callable, arguments);
        NativeDatum result = await InvokeDatumAsync(callable,
            arguments.Select(static value => new NativeDatum(value)).ToArray(), cancellationToken);
        return await result.ResolveAsync(cancellationToken);
    }

    private ValueTask<NativeDatum> InvokeDatumAsync(NativeValue callable,
        IReadOnlyList<NativeDatum> arguments, CancellationToken token)
    {
        NativeFunction function = callable.AsFunction();
        if (function.NativeName is { } name)
            return InvokeNativeDatumAsync(name, arguments, context.CaptureDirectory(), null, token);
        ValidateInvocation(function.Function, arguments.Count);
        return new(RunFunction(function.Function, function.Environment, new(arguments), token));
    }

    private void ValidateInvocation(int index, int count)
    {
        NativeFunctionCode function = image.Program.Functions[index];
        if (function.IsTemplate) throw new MsyncException("The selected callable requires a compiled specialization.");
        if (count != function.ParameterCount)
            throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {count}.");
    }

    private void RunActivation(Activation activation, CancellationToken token, ref TierInvocation? tier, int pc = 0, bool entering = true)
    {
        NativeExecutionFunction executable = activation.Execution;
        bool enableTiering = !disposed && !executable.IsUnavailable && (tierOptions.Policy == NativeJitPolicy.Force
            || tierOptions.Policy == NativeJitPolicy.Auto && NativeMachineCode.AutoEnabled);
        if (enableTiering && entering) executable.Enter();
        bool linkedCounters = tierOptions.Policy == NativeJitPolicy.Linked;
        bool observed = profile is not null;
        if (linkedCounters && entering) { tierCounters.LinkedEntry(); activation.Work.Counters.LinkedEntry(); }
        RunActivationFromSync(activation, pc, null, true, enableTiering, linkedCounters, observed, token, ref tier);
    }

    private void RunActivationFromSync(Activation activation, int pc, NativeCompiledFunction? version,
        bool promotionPoint, bool enableTiering, bool linkedCounters, bool observed, CancellationToken token, ref TierInvocation? tier)
    {
        NativeFunctionCode function = activation.Function;
        NativeFrame frame = activation.Frame;
        NativeWorkScope owner = activation.Work;
        NativeExecutionFunction executable = activation.Execution;
        NativeInstruction[] instructions = executable.Metadata;
        while (pc < instructions.Length)
        {
            token.ThrowIfCancellationRequested();
            if (enableTiering && promotionPoint)
            {
                version = executable.TryCompile(tierCounters, owner.Counters);
                if (version is null && executable.IsUnavailable) enableTiering = false;
                promotionPoint = false;
            }
            if (version is not null)
            {
                TierOutcome outcome = RunTier(activation, executable, version, pc, token, ref tier);
                if (outcome.Returned) return;
                pc = outcome.Next;
                if (!outcome.ExecuteBaseline) continue;
                version = null;
            }
            if (enableTiering && executable.HasObservableBinary) ObserveTier(executable, frame, pc);
            NativeStep step;
            if (observed) executable.Visit(owner.Counters, pc);
            if (linkedCounters) { tierCounters.LinkedOperation(); owner.Counters.LinkedOperation(); }
            try
            {
                NativeStep pending = ExecuteLinked(activation, executable.Instructions[pc], pc, token);
                step = DemandStep(pending, activation, pc);
            }
            catch (Exception error)
            {
                ExceptionDispatchInfo.Capture(WrapStepFailure(executable, function, pc, error)).Throw();
                throw;
            }
            if (step.Returned) return;
            if (observed) owner.Counters.Edge(pc + executable.Instructions[pc].Span - 1, step.Next);
            if (step.Next <= pc) { if (enableTiering) executable.Backedge(); promotionPoint = true; }
            pc = step.Next;
        }
        ReturnSlot(activation, -1);
    }

    private static Exception WrapStepFailure(NativeExecutionFunction executable, NativeFunctionCode function, int pc, Exception error)
    {
        if (error is OperationCanceledException) return error;
        if (error is MsyncException msync)
            return msync.Span is null && executable.Diagnostic(pc).Source is { } source ? new MsyncException(msync.Message, source, msync) : msync;
        if (error is InvalidOperationException)
        {
            NativeInstruction instruction = executable.Diagnostic(pc);
            return new MsyncException($"{error.Message} Native {instruction.Op} in function '{function.Name}', instruction {pc}, operands {instruction.A}/{instruction.B}/{instruction.C}, result {instruction.Result}.", instruction.Source, error);
        }
        if (error is ArgumentException or IOException or UnauthorizedAccessException)
            return new MsyncException(error.Message, executable.Diagnostic(pc).Source, error);
        return error;
    }

    private async ValueTask FinishOwnedWorkAsync(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, NativeDatum result, CancellationToken token)
    {
        Exception? iteratorFailure = null;
        try { await activation.CloseIteratorsAsync(); }
        catch (Exception error) { iteratorFailure = error; }
        if (activation.OwnsWork && activation.Work.HasWork)
        {
            NativeReachable reachable = new(memory, GetRangeRoots, image);
            reachable.Add(result);
            for (int index = 0; index < arguments.Count; index++) reachable.Add(arguments.Read(index, memory));
            foreach (StaticState state in statics)
                if (Volatile.Read(ref state.Allocation) is { } allocation)
                    reachable.Add(new NativeDatum(NativeValue.FromAddress(allocation.Address)));
            if (environment is not null)
                for (int capture = 0; capture < activation.Function.CaptureCount; capture++)
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
    private readonly struct NativeStep(int next, bool returned = false, Task<NativeStep>? pending = null)
    {
        internal readonly int Next = next;
        internal readonly bool Returned = returned;
        internal readonly Task<NativeStep>? Pending = pending;

        public static implicit operator NativeStep(ValueTask<NativeStep> step) =>
            step.IsCompleted ? step.Result : new(-1, pending: step.AsTask());
    }

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

    internal sealed class Activation
    {
        internal Activation(int functionIndex, NativeFunctionCode function, NativeFrame frame, NativeExecutionFunction execution)
        {
            FunctionIndex = functionIndex;
            Function = function;
            Frame = frame;
            Execution = execution;
            Iterators = function.IteratorCount == 0 ? [] : new IAsyncEnumerator<NativeDatum>?[function.IteratorCount];
        }
        internal NativeExecutionFunction Execution { get; }
        internal int FunctionIndex { get; }
        internal NativeFunctionCode Function { get; }
        internal NativeFrame Frame { get; }
        internal NativeWorkScope Work { get; private set; } = null!;
        internal bool OwnsWork { get; private set; }
        internal NativeDatum Result { get; set; }
        internal NativeAddress ReturnDestination;
        internal TailInvocation? Tail { get; set; }
        internal GuestCall? Call { get; set; }
        internal IAsyncEnumerator<NativeDatum>?[] Iterators { get; }
        internal Dictionary<int, NativeDatum[]>? Plans;
        internal Dictionary<string, HashSet<NativeFuture>>? Regions;

        internal void Enter(NativeWorkScope work, bool ownsWork) => (Work, OwnsWork) = (work, ownsWork);

        internal void Clear()
        {
            Work = null!;
            Result = default;
            ReturnDestination = default;
            Tail = null;
            Call = null;
            Plans = null;
            Regions = null;
        }
        internal bool HasActiveIterators
        {
            get
            {
                for (int index = 0; index < Iterators.Length; index++)
                    if (Iterators[index] is not null) return true;
                return false;
            }
        }
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
        executionWork.AbortOwnedWork();
        try { await executionWork.FinishAsync(new(memory, GetRangeRoots, image), () => Interlocked.Increment(ref failedTransfers), CancellationToken.None); }
        finally
        {
            foreach (KeyValuePair<NativeImage, NativeExecutionImage> entry in executableImages) entry.Value.Dispose();
            DisposeTierPool();
            transferSlots.Dispose();
            await sessionContext.Connections.ReleaseAsync(sessionContext);
        }
    }
}
