using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Module module;
    private readonly RuntimeContext context;
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
        planResources = transferredResources ?? previous?.planResources ?? new();
        resources = previous?.resources ?? new();
        pending = previous?.pending ?? [];
        sessionProcesses = previous?.sessionProcesses ?? new(ReferenceEqualityComparer.Instance);
        processDependencies = previous?.processDependencies ?? [];
        namespaceProducers = previous?.namespaceProducers ?? [];
        iterators = previous?.iterators ?? [];
        context = new(options, module.Context);
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
        try { return await ResolveResultAsync(await RunFunctionAsync(module.EntryPoint, [], [], cancellationToken)); }
        finally
        {
            try
            {
                await DrainAsync();
                foreach (var iterator in iterators) await iterator.DisposeAsync();
            }
            finally { await DisposePlanResourcesAsync(); }
        }
    }
    internal async ValueTask<Value> ExecuteEntryAsync(Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        try { return await ResolveAsync(await RunCodeAsync(module.EntryPoint, locals, startInstruction, cancellationToken)); }
        finally
        {
            await JoinTransfersAsync(includeProcessFlows: false);
            RetireSessionWork();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
            iterators.Clear();
        }
    }
    private ValueTask<Value> RunFunctionAsync(int functionIndex, IReadOnlyList<Value> captures, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        var function = CallLayout(functionIndex, arguments.Count);
        var locals = InitializeFrame(function, captures);
        for (var i = 0; i < arguments.Count; i++) locals[function.CaptureCount + i] = arguments[i];
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, arguments);
    }

    private ValueTask<Value> RunStackFunctionAsync(int functionIndex, FunctionValue callee, int count,
        ref OperandStack operands, CancellationToken cancellationToken)
    {
        var function = CallLayout(functionIndex, count);
        var captures = callee.Captures ?? [];
        var locals = InitializeFrame(function, captures);
        var arguments = operands.CopyArguments(count, locals, function.CaptureCount);
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, callerArguments: arguments);
    }

    private FunctionCode CallLayout(int functionIndex, int argumentCount)
    {
        RefreshSessionImage();
        var function = module.Functions[functionIndex];
        if (argumentCount != function.ParameterCount) throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {argumentCount}");
        return function;
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
    private async ValueTask<Value> RunCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, IReadOnlyList<Value>? captures = null, IReadOnlyList<Value>? arguments = null,
        ArraySegment<Value?> callerArguments = default)
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
            result = await RunOwnedCodeAsync(functionIndex, locals, startInstruction, cancellationToken);
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
                    await JoinOwnerAsync(currentOwner);
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
