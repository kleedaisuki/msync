using Msync.Core.Native;

namespace Msync.Runtime.Native;

// One interpreter owns the session's roots, static declarations and escaping work.
public sealed class NativeExecutionSession : IAsyncDisposable
{
    private readonly ExecutionOptions? options;
    private readonly NativeRuntimeContext context;
    private NativeInterpreter? interpreter;

    public NativeExecutionSession(ExecutionOptions? options = null)
    {
        this.options = options;
        context = new(options);
    }

    public string WorkingDirectory => context.WorkingDirectory;
    public NativeTierStatistics TierStatistics => interpreter?.TierStatistics ?? default;
    public void ChangeWorkingDirectory(string path) => context.ChangeWorkingDirectory(NativeValue.FromString(path));
    public bool IsLocalInitialized(int slot) => interpreter?.IsLocalInitialized(slot) == true;
    public NativeValue? GetLocalValue(int slot) => interpreter?.GetLocalValue(slot);
    internal NativeLayout ScopeLayout(NativeAddress address) => interpreter!.ScopeLayout(address);
    internal ValueTask<NativeValue?> ObserveMemberAsync(NativeAddress address, CancellationToken token) =>
        interpreter!.ObserveMemberAsync(address, token);

    public ValueTask<NativeValue> ExecuteAsync(NativeProgram program, CancellationToken cancellationToken = default) =>
        Prepare(program).ExecuteAsync(cancellationToken);

    internal NativeValue ExecuteEntry(NativeProgram program, CancellationToken cancellationToken) =>
        Prepare(program).ExecuteEntry(cancellationToken);

    private NativeInterpreter Prepare(NativeProgram program)
    {
        context.AdoptAmbientDirectory();
        program = program with { PreserveLocals = true };
        if (interpreter is null) interpreter = new(program, options, context);
        else interpreter.UpdateProgram(program);
        return interpreter;
    }

    internal void CollectGuestHeap() => interpreter?.CollectGuestHeap();

    public ValueTask DisposeAsync() => interpreter?.DisposeAsync() ?? ValueTask.CompletedTask;
}
