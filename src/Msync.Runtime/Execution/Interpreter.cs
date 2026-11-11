using System.Globalization;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Module module;
    private readonly RuntimeContext context;
    private readonly RuntimeProfile? profile;
    private readonly ContextualResources planResources;
    private readonly SemaphoreSlim slots;
    private readonly int transferConcurrency;
    private readonly HashSet<IAsyncDisposable> iterators;
    private readonly Dictionary<string, IReadOnlyDictionary<int, int>> dispatchTables = [];
    private int failedTransfers;
    private int callDepth;
    public int FailedTransfers => failedTransfers;

    public Interpreter(Module module, ExecutionOptions? options = null) : this(module, options, null) { }
    public Interpreter(Module module, ExecutionOptions? options, CancellationToken cancellationToken)
        : this(module, options, null, cancellationToken) { }
    internal Interpreter(Module module, ExecutionOptions? options, Interpreter? previous,
        CancellationToken cancellationToken = default, ContextualResources? transferredResources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        planResources = transferredResources ?? previous?.planResources ?? new(default, options?.Timeline);
        resources = previous?.resources ?? new();
        pending = previous?.pending ?? [];
        sessionProcesses = previous?.sessionProcesses ?? new(ReferenceEqualityComparer.Instance);
        processDependencies = previous?.processDependencies ?? [];
        namespaceProducers = previous?.namespaceProducers ?? new();
        iterators = previous?.iterators ?? [];
        context = new(options, module.Context);
        profile = options?.Profile;
        try { this.module = Binding.Relocate(module, ImageContext); }
        catch
        {
            if (previous is null && transferredResources is null) planResources.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        transferConcurrency = previous?.transferConcurrency
            ?? Math.Max(1, Math.Min(context.Jobs, (int)Math.Min(int.MaxValue, context.MemoryLimit / Math.Max(1, context.BufferSize))));
        slots = previous?.slots ?? new(transferConcurrency);
    }
    public static ValueTask<Value> ExecuteAsync(Module module, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => new Interpreter(module, options, cancellationToken).ExecuteAsync(cancellationToken);
    public async ValueTask<Value> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        RuntimeFrameTrace? trace = context.Timeline is { } timeline ? new(timeline) : null;
        try
        {
            var result = ResolveResultAsync(await RunFunctionAsync(module.EntryPoint, [], [], cancellationToken));
            return await (trace?.Observe(result, "final_result") ?? result);
        }
        finally
        {
            try
            {
                var drain = DrainAsync();
                await (trace?.Observe(drain, "execution_drain") ?? drain);
                foreach (var iterator in iterators)
                {
                    var dispose = iterator.DisposeAsync();
                    await (trace?.Observe(dispose, "iterator_cleanup") ?? dispose);
                }
            }
            finally
            {
                var dispose = DisposePlanResourcesAsync();
                await (trace?.Observe(dispose, "plan_resources") ?? dispose);
            }
        }
    }
    internal async ValueTask<Value> ExecuteEntryAsync(Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        RuntimeFrameTrace? trace = context.Timeline is { } timeline ? new(timeline) : null;
        try
        {
            var result = ResolveAsync(await RunCodeAsync(module.EntryPoint, locals, startInstruction, cancellationToken));
            return await (trace?.Observe(result, "submission_result") ?? result);
        }
        finally
        {
            var joined = JoinTransfersAsync(includeProcessFlows: false);
            await (trace?.Observe(joined, "submission_join") ?? joined);
            RetireSessionWork();
            foreach (var iterator in iterators)
            {
                var dispose = iterator.DisposeAsync();
                await (trace?.Observe(dispose, "iterator_cleanup") ?? dispose);
            }
            iterators.Clear();
        }
    }
    private ValueTask<Value> RunFunctionAsync(int functionIndex, IReadOnlyList<Value> captures, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        RefreshSessionImage();
        var function = module.Functions[functionIndex];
        RequireArity(function, arguments.Count);
        var locals = InitializeFrame(function, captures);
        for (var i = 0; i < arguments.Count; i++) locals[function.CaptureCount + i] = arguments[i];
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, arguments);
    }

    private ValueTask<Value> RunStackFunctionAsync<TFrame>(int functionIndex, FunctionValue callee, int count,
        ref OperandStack<TFrame> operands, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        var function = module.Functions[functionIndex];
        var captures = callee.Captures ?? [];
        int captured = captures.Count;
        // The caller cannot pop this slice until the callee's work has joined.
        // A single capture fits in the now-dead callee operand; the argument
        // slice still describes the caller's original, once-normalized roots.
        if (sessionImage is null && captured <= 1 && captured == function.CaptureCount
            && count == function.ParameterCount && function.LocalCount == count + captured)
        {
            var borrowed = operands.BorrowArguments(count, captured == 0 ? null : captures[0]);
            return RunCodeAsync(functionIndex, (Value[])borrowed.Array!, 0, cancellationToken, captures,
                callerArguments: borrowed, borrowedAt: borrowed.Offset - captured);
        }
        var locals = InitializeFrame(function, captures);
        var arguments = operands.CopyArguments(count, locals, function.CaptureCount);
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, callerArguments: arguments);
    }

    private static void RequireArity(FunctionCode function, int argumentCount)
    {
        if (argumentCount != function.ParameterCount) throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {argumentCount}");
    }

    private static Value[] InitializeFrame(FunctionCode function, IReadOnlyList<Value> captures)
    {
        Value[] locals = function.LocalCount == 0 ? [] : new Value[function.LocalCount];
        for (var i = 0; i < captures.Count; i++) locals[i] = captures[i];
        for (var i = captures.Count; i < function.CaptureCount; i++) locals[i] = Value.Null;
        for (var i = Math.Max(captures.Count, function.CaptureCount + function.ParameterCount); i < locals.Length; i++)
            locals[i] = Value.Null;
        return locals;
    }
    private ValueTask<Value> RunCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, IReadOnlyList<Value>? captures = null, IReadOnlyList<Value>? arguments = null,
        ArraySegment<Value?> callerArguments = default, int borrowedAt = -1) =>
        context.Timeline is { } timeline
            ? RunObservedCodeAsync(timeline, functionIndex, locals, startInstruction, cancellationToken, captures, arguments, callerArguments, borrowedAt)
            : RunCodeCoreAsync(functionIndex, locals, startInstruction, cancellationToken, captures, arguments, callerArguments, borrowedAt);

    private async ValueTask<Value> RunObservedCodeAsync(RuntimeTimeline timeline, int functionIndex, Value[] locals,
        int startInstruction, CancellationToken cancellationToken, IReadOnlyList<Value>? captures,
        IReadOnlyList<Value>? arguments, ArraySegment<Value?> callerArguments, int borrowedAt)
    {
        int localCount = borrowedAt < 0 ? locals.Length : module.Functions[functionIndex].LocalCount;
        using var span = BeginFrame(timeline, functionIndex, startInstruction, localCount);
        span.SetAttribute("frame.storage", borrowedAt < 0 ? "private" : "caller_slice");
        RuntimeFrameTrace trace = new(timeline);
        try
        {
            Value value = await RunCodeCoreAsync(functionIndex, locals, startInstruction, cancellationToken, captures, arguments, callerArguments, borrowedAt, trace);
            span.SetAttribute("frame.exit", "return");
            return value;
        }
        catch (Exception error) { span.Fail(error); throw; }
        finally { span.SetMetric("suspensions", trace.Suspensions); }
    }

    private RuntimeTimelineScope BeginFrame(RuntimeTimeline timeline, int functionIndex, int startInstruction, int locals)
    {
        Module image = module;
        var function = image.Functions[functionIndex];
        var span = timeline.Begin("vm.frame");
        span.SetAttribute("function.id", functionIndex.ToString(CultureInfo.InvariantCulture));
        span.SetAttribute("function.name", function.Name);
        if (profile is { } recorder) span.SetAttribute("image.id", recorder.ImageId(image).ToString(CultureInfo.InvariantCulture));
        span.SetAttribute("artifact.stage", image.Stage.ToString());
        span.SetAttribute("optimization.level", image.OptimizationLevel.ToString(CultureInfo.InvariantCulture));
        span.SetAttribute("duration.kind", "inclusive");
        span.SetMetric("entry.pc", startInstruction);
        span.SetMetric("frame.locals", locals);
        span.SetMetric("frame.parameters", function.ParameterCount);
        span.SetMetric("frame.captures", function.CaptureCount);
        if (function.Instructions is Instruction[] code && (uint)startInstruction < (uint)code.Length)
            span.SetSource(code[startInstruction].Span);
        return span;
    }

    private async ValueTask<Value> RunCodeCoreAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, IReadOnlyList<Value>? captures, IReadOnlyList<Value>? arguments,
        ArraySegment<Value?> callerArguments, int borrowedAt, RuntimeFrameTrace? trace = null)
    {
        var active = sessionImage?.ActiveInterpreter;
        var outer = active is not null && !ReferenceEquals(active, this) ? active : null;
        var previous = currentOwner;
        var caller = outer?.currentOwner ?? previous;
        bool hasCaller = outer is not null || callDepth != 0;
        callDepth++;
        if (sessionImage is { } session) session.ActiveInterpreter = this;
        currentOwner = default;
        Value? result = null;
        try
        {
            result = await RunOwnedCodeAsync(functionIndex, locals, startInstruction, cancellationToken, trace, borrowedAt);
            return result;
        }
        finally
        {
            try
            {
                if (currentOwner.Processes is { Count: > 0 } || currentOwner.Tasks is { Count: > 0 })
                {
                    var roots = sessionImage is not null && functionIndex == module.EntryPoint ? module.SessionRoots : null;
                    // The calling frame stays suspended until ownership has been resolved.
                    // Inspect the original arguments, not possibly rebound parameters.
                    if (callerArguments.Count != 0 && currentOwner.Processes is { Count: > 0 })
                        arguments = MaterializeArgumentRoots(callerArguments);
                    PromoteReturnedProcesses(result, roots, locals, captures, arguments, ref caller, hasCaller);
                    var joined = JoinOwnerAsync(currentOwner);
                    await (trace?.Observe(joined, "ownership_join") ?? joined);
                }
            }
            finally
            {
                currentOwner = outer is null ? caller : previous;
                if (outer is not null) outer.currentOwner = caller;
                callDepth--;
                if (sessionImage is { } image) image.ActiveInterpreter = active;
            }
        }
    }

    private static Value[] MaterializeArgumentRoots(ArraySegment<Value?> arguments)
    {
        var values = new Value[arguments.Count];
        for (int index = 0; index < values.Length; index++)
            values[index] = arguments.Array![arguments.Offset + index] ?? Value.Null;
        return values;
    }
}
