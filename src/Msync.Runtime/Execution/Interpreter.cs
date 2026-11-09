using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Module module;
    private readonly RuntimeContext context;
    private BasicBlock[][] blocks;
    private readonly SemaphoreSlim slots;
    private readonly HashSet<IAsyncDisposable> iterators;
    private readonly Dictionary<string, IReadOnlyDictionary<int, int>> dispatchTables = [];
    private int failedTransfers;
    private int callDepth;
    public int FailedTransfers => failedTransfers;

    public Interpreter(Module module, ExecutionOptions? options = null) : this(module, options, null) { }
    internal Interpreter(Module module, ExecutionOptions? options, Interpreter? previous)
    {
        resources = previous?.resources ?? new();
        pending = previous?.pending ?? [];
        sessionProcesses = previous?.sessionProcesses ?? new(ReferenceEqualityComparer.Instance);
        processDependencies = previous?.processDependencies ?? [];
        namespaceProducers = previous?.namespaceProducers ?? [];
        iterators = previous?.iterators ?? [];
        context = new(options, module.Context);
        (this.module, blocks) = ProjectImage(module);
        slots = previous?.slots ?? new(Math.Max(1, Math.Min(context.Jobs, (int)Math.Min(int.MaxValue, context.MemoryLimit / Math.Max(1, context.BufferSize)))));
    }
    public static ValueTask<Value> ExecuteAsync(Module module, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => new Interpreter(module, options).ExecuteAsync(cancellationToken);
    public async ValueTask<Value> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try { return await ResolveResultAsync(await RunFunctionAsync(module.EntryPoint, [], [], cancellationToken)); }
        finally
        {
            await DrainAsync();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
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
        RefreshSessionImage();
        var function = module.Functions[functionIndex];
        if (arguments.Count != function.ParameterCount) throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {arguments.Count}");
        var locals = new Value[function.LocalCount];
        Array.Fill(locals, Value.Null);
        for (var i = 0; i < captures.Count; i++) locals[i] = captures[i];
        for (var i = 0; i < arguments.Count; i++) locals[function.CaptureCount + i] = ReadValue(arguments[i]);
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, arguments);
    }
    private async ValueTask<Value> RunCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, IReadOnlyList<Value>? captures = null, IReadOnlyList<Value>? arguments = null)
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
                var roots = sessionImage is not null && functionIndex == module.EntryPoint ? module.SessionRoots : null;
                PromoteReturnedProcesses(result, roots, locals, captures, arguments, ref caller, hasCaller);
                await JoinOwnerAsync(currentOwner);
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
}
