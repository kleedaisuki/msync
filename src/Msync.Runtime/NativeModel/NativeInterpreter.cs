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

    public async ValueTask<NativeValue> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeWorkScope? previous = currentWork.Value;
        Generation? previousGeneration = activeGeneration.Value;
        currentWork.Value = executionWork;
        activeGeneration.Value = generation;
        try
        {
            ValidateInvocation(image.Program.EntryPoint, 0);
            NativeDatum result = await RunFunctionAsync(image.Program.EntryPoint, null, default, cancellationToken);
            return result.Future is { } future ? await future.CompleteAsync(cancellationToken) : result.Value;
        }
        finally
        {
            executionWork.CountCompletedTransfers(() => Interlocked.Increment(ref failedTransfers));
            currentWork.Value = previous;
            activeGeneration.Value = previousGeneration;
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

    private ValueTask<NativeDatum> InvokeDatumAsync(NativeValue callable,
        IReadOnlyList<NativeDatum> arguments, CancellationToken token)
    {
        NativeFunction function = callable.AsFunction();
        if (function.NativeName is { } name)
            return InvokeNativeDatumAsync(name, arguments, context.CaptureDirectory(), null, token);
        ValidateInvocation(function.Function, arguments.Count);
        return RunFunctionAsync(function.Function, function.Environment, new(arguments), token);
    }

    private void ValidateInvocation(int index, int count)
    {
        NativeFunctionCode function = image.Program.Functions[index];
        if (function.IsTemplate) throw new MsyncException("The selected callable requires a compiled specialization.");
        if (count != function.ParameterCount)
            throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {count}.");
    }

    private ValueTask<NativeDatum> RunFunctionAsync(int index, NativeEnvironment? environment,
        NativeArguments arguments, CancellationToken token)
    {
        Generation? previousGeneration = programUpdated ? activeGeneration.Value : generation;
        Generation selected = previousGeneration ?? generation;
        bool switchGeneration = !ReferenceEquals(selected, previousGeneration);
        NativeWorkScope? parent = currentWork.Value;
        return RunFunctionSync(index, environment, arguments, token, selected, switchGeneration, previousGeneration, parent, null, null);
    }

    private ValueTask<NativeDatum> RunFunctionSync(int index, NativeEnvironment? environment, NativeArguments arguments,
        CancellationToken token, Generation selected, bool switchGeneration, Generation? previousGeneration,
        NativeWorkScope? parent, List<TailReturn>? returns, TailInvocation? origin)
    {
        Dictionary<NativeLayout, NativeAllocation>? tailBuffers = null;
        NativeAllocation? currentTailBuffer = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                NativeFunctionCode function = selected.Image.Program.Functions[index];
                NativeExecutionFunction executable = ExecutionFunction(selected.Image, index);
                bool trackScope = profile is not null || !executable.IsPureSynchronous;
                NativeWorkScope owner = trackScope
                    ? new(parent ?? executionWork) { Counters = profile?.EnterNative(selected.Image.Program, index) ?? default }
                    : parent ?? executionWork;
                NativeFrame frame = executable.EnterFrame(environment, arguments, memory,
                    index == selected.Image.Program.EntryPoint && selected.Image.Program.PreserveLocals ? sessionLocations : null,
                    out bool reused);
                if (profile is not null) owner.Counters.CallStorage(arguments.BorrowedCount, arguments.Count - arguments.BorrowedCount, arguments.IsSnapshot, reused);
                Activation activation = new(index, function, frame, owner, executable, trackScope);
                NativeDatum result = new(NativeValue.Null);
                if (switchGeneration) activeGeneration.Value = selected;
                if (trackScope) currentWork.Value = owner;
                ValueTask<NativeDatum> run = default;
                ExceptionDispatchInfo? runFailure = null;
                try
                {
                    run = RunActivationAsync(activation, token);
                    if (!run.IsCompleted)
                        return AwaitFunctionRunAsync(environment, arguments, token, selected, switchGeneration,
                            previousGeneration, parent, returns, origin, function, executable, frame, owner, activation, run);
                    result = run.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    runFailure = ExceptionDispatchInfo.Capture(error);
                }
                ValueTask finish;
                try { finish = FinishActivationAsync(activation, environment, arguments, result, token); }
                catch
                {
                    if (trackScope) currentWork.Value = parent;
                    if (switchGeneration) activeGeneration.Value = previousGeneration;
                    throw;
                }
                if (!finish.IsCompleted)
                    return AwaitFunctionFinishAsync(finish, runFailure, token, selected, switchGeneration,
                        previousGeneration, parent, returns, origin, function, executable, frame, owner, activation, result);
                try { finish.GetAwaiter().GetResult(); }
                finally
                {
                    if (trackScope) currentWork.Value = parent;
                    if (switchGeneration) activeGeneration.Value = previousGeneration;
                }
                if (runFailure is not null) runFailure.Throw();
                if (activation.Tail is not { } tail)
                {
                    executable.LeaveFrame(frame, memory);
                    if (currentTailBuffer is not null) memory.ResetFrame(currentTailBuffer);
                    if (returns is not null)
                        for (int pending = returns.Count - 1; pending >= 0; pending--) returns[pending].Complete();
                    return new(result);
                }
                NativeArguments nextArguments = default;
                NativeAllocation? nextBuffer = null;
                if (tail.Arguments.Count != 0)
                {
                    FrameAllocation target = selected.Image.Frame(tail.Target);
                    tailBuffers ??= [];
                    bool allocated = !tailBuffers.TryGetValue(target.Parameters, out nextBuffer);
                    if (allocated) tailBuffers.Add(target.Parameters, nextBuffer = new(target.Parameters));
                    tail.Arguments.CopyTo(nextBuffer!, memory);
                    nextArguments = new(nextBuffer!, target.ParameterBorrows, target.ParameterBorrowCount);
                    owner.Counters.TailArguments(tail.Arguments.Count, allocated);
                }
                if (currentTailBuffer is not null && !ReferenceEquals(currentTailBuffer, nextBuffer)) memory.ResetFrame(currentTailBuffer);
                currentTailBuffer = nextBuffer;
                executable.LeaveFrame(frame, memory);
                if (profile is not null) (returns ??= []).Add(new(owner.Counters, function, tail.Pc, tail.Plan));
                origin = tail;
                index = tail.Target;
                environment = tail.Environment;
                arguments = nextArguments;
            }
        }
        catch (Exception error) when (origin is not null)
        {
            ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error);
            throw;
        }
    }

    private async ValueTask<NativeDatum> AwaitFunctionRunAsync(NativeEnvironment? environment, NativeArguments arguments,
        CancellationToken token, Generation selected, bool switchGeneration, Generation? previousGeneration,
        NativeWorkScope? parent, List<TailReturn>? returns, TailInvocation? origin,
        NativeFunctionCode function, NativeExecutionFunction executable, NativeFrame frame,
        NativeWorkScope owner, Activation activation, ValueTask<NativeDatum> run)
    {
        NativeDatum result = new(NativeValue.Null);
        try
        {
            try { result = await run; }
            finally
            {
                try { await FinishActivationAsync(activation, environment, arguments, result, token); }
                finally
                {
                    currentWork.Value = parent;
                    if (switchGeneration) activeGeneration.Value = previousGeneration;
                }
            }
        }
        catch (Exception error) when (origin is not null)
        {
            ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error);
            throw;
        }
        return await CompleteTailOrReturnAsync(result, token, selected, switchGeneration, previousGeneration,
            parent, returns, function, executable, frame, owner, activation);
    }

    private async ValueTask<NativeDatum> AwaitFunctionFinishAsync(ValueTask finish, ExceptionDispatchInfo? runFailure,
        CancellationToken token, Generation selected, bool switchGeneration, Generation? previousGeneration,
        NativeWorkScope? parent, List<TailReturn>? returns, TailInvocation? origin,
        NativeFunctionCode function, NativeExecutionFunction executable, NativeFrame frame,
        NativeWorkScope owner, Activation activation, NativeDatum result)
    {
        try
        {
            try { await finish; }
            finally
            {
                currentWork.Value = parent;
                if (switchGeneration) activeGeneration.Value = previousGeneration;
            }
            if (runFailure is not null) runFailure.Throw();
        }
        catch (Exception error) when (origin is not null)
        {
            ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error);
            throw;
        }
        return await CompleteTailOrReturnAsync(result, token, selected, switchGeneration, previousGeneration,
            parent, returns, function, executable, frame, owner, activation);
    }

    private ValueTask<NativeDatum> CompleteTailOrReturnAsync(NativeDatum result, CancellationToken token,
        Generation selected, bool switchGeneration, Generation? previousGeneration, NativeWorkScope? parent,
        List<TailReturn>? returns, NativeFunctionCode function,
        NativeExecutionFunction executable, NativeFrame frame, NativeWorkScope owner, Activation activation)
    {
        if (activation.Tail?.Arguments.IsFrameView != true) executable.LeaveFrame(frame, memory);
        if (activation.Tail is not { } tail)
        {
            if (returns is not null)
                for (int pending = returns.Count - 1; pending >= 0; pending--) returns[pending].Complete();
            return new(result);
        }
        if (profile is not null) (returns ??= []).Add(new(owner.Counters, function, tail.Pc, tail.Plan));
        return RunFunctionSync(tail.Target, tail.Environment, tail.Arguments, token, selected, switchGeneration,
            previousGeneration, parent, returns, tail);
    }

    private ValueTask<NativeDatum> RunActivationAsync(Activation activation, CancellationToken token)
    {
        NativeExecutionFunction executable = activation.Execution;
        bool enableTiering = !disposed && !executable.IsUnavailable && (tierOptions.Policy == NativeJitPolicy.Force
            || tierOptions.Policy == NativeJitPolicy.Auto && NativeMachineCode.AutoEnabled);
        if (enableTiering) executable.Enter();
        bool linkedCounters = tierOptions.Policy == NativeJitPolicy.Linked;
        bool observed = profile is not null;
        if (linkedCounters) { tierCounters.LinkedEntry(); activation.Work.Counters.LinkedEntry(); }
        return RunActivationFromSync(activation, 0, null, true, enableTiering, linkedCounters, observed, token);
    }

    private ValueTask<NativeDatum> RunActivationFromSync(Activation activation, int pc, NativeCompiledFunction? version,
        bool promotionPoint, bool enableTiering, bool linkedCounters, bool observed, CancellationToken token)
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
                ValueTask<TierOutcome> tierPending = RunTier(activation, executable, version, pc, token);
                if (!tierPending.IsCompleted)
                    return AwaitActivationTierAsync(activation, version, enableTiering, linkedCounters, observed, tierPending, token);
                TierOutcome outcome = tierPending.GetAwaiter().GetResult();
                if (outcome.Returned) return new(activation.Result);
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
                ValueTask<NativeStep> pending = ExecuteLinked(activation, executable.Instructions[pc], pc, token);
                if (!pending.IsCompleted)
                    return AwaitActivationStepAsync(activation, pc, version, promotionPoint, enableTiering, linkedCounters, observed, pending, token);
                step = pending.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                ExceptionDispatchInfo.Capture(WrapStepFailure(executable, function, pc, error)).Throw();
                throw;
            }
            if (step.Returned) return new(activation.Result);
            if (observed) owner.Counters.Edge(pc + executable.Instructions[pc].Span - 1, step.Next);
            if (step.Next <= pc) { if (enableTiering) executable.Backedge(); promotionPoint = true; }
            pc = step.Next;
        }
        return new(new NativeDatum(NativeValue.Null));
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

    private async ValueTask<NativeDatum> AwaitActivationTierAsync(Activation activation, NativeCompiledFunction version,
        bool enableTiering, bool linkedCounters, bool observed, ValueTask<TierOutcome> tierPending, CancellationToken token)
    {
        TierOutcome outcome = await tierPending;
        if (outcome.Returned) return activation.Result;
        NativeCompiledFunction? nextVersion = outcome.ExecuteBaseline ? null : version;
        return await RunActivationFromSync(activation, outcome.Next, nextVersion, false, enableTiering, linkedCounters, observed, token);
    }

    private async ValueTask<NativeDatum> AwaitActivationStepAsync(Activation activation, int pc, NativeCompiledFunction? version,
        bool promotionPoint, bool enableTiering, bool linkedCounters, bool observed, ValueTask<NativeStep> pending, CancellationToken token)
    {
        NativeExecutionFunction executable = activation.Execution;
        NativeWorkScope owner = activation.Work;
        NativeStep step;
        owner.Counters.Pause();
        try
        {
            try { step = await pending; }
            finally { owner.Counters.Continue(); }
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo.Capture(WrapStepFailure(executable, activation.Function, pc, error)).Throw();
            throw;
        }
        if (step.Returned) return activation.Result;
        if (observed) owner.Counters.Edge(pc + executable.Instructions[pc].Span - 1, step.Next);
        if (step.Next <= pc) { if (enableTiering) executable.Backedge(); promotionPoint = true; }
        return await RunActivationFromSync(activation, step.Next, version, promotionPoint, enableTiering, linkedCounters, observed, token);
    }

    private ValueTask FinishActivationAsync(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, NativeDatum result, CancellationToken token) =>
        !activation.HasActiveIterators && (!activation.OwnsWork || !activation.Work.HasWork)
            ? ValueTask.CompletedTask
            : AwaitFinishActivationAsync(activation, environment, arguments, result, token);

    private async ValueTask AwaitFinishActivationAsync(Activation activation, NativeEnvironment? environment,
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

    private sealed class Activation(int functionIndex, NativeFunctionCode function, NativeFrame frame, NativeWorkScope work, NativeExecutionFunction execution, bool ownsWork = true)
    {
        internal NativeExecutionFunction Execution { get; } = execution;
        internal int FunctionIndex { get; } = functionIndex;
        internal NativeFunctionCode Function { get; } = function;
        internal NativeFrame Frame { get; } = frame;
        internal NativeWorkScope Work { get; } = work;
        internal bool OwnsWork { get; } = ownsWork;
        internal NativeDatum Result { get; set; }
        internal TailInvocation? Tail { get; set; }
        internal IAsyncEnumerator<NativeDatum>?[] Iterators { get; } = function.IteratorCount == 0 ? [] : new IAsyncEnumerator<NativeDatum>?[function.IteratorCount];
        internal Dictionary<int, NativeDatum[]>? Plans;
        internal Dictionary<string, HashSet<NativeFuture>>? Regions;
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
        executionWork.AbortProcesses();
        try { await executionWork.FinishAsync(new(memory, GetRangeRoots, image), () => Interlocked.Increment(ref failedTransfers), CancellationToken.None); }
        finally
        {
            foreach (KeyValuePair<NativeImage, NativeExecutionImage> entry in executableImages) entry.Value.Dispose();
            DisposeTierPool();
            transferSlots.Dispose();
        }
    }
}
