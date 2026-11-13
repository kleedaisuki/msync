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

    public ValueTask<NativeValue> ExecuteAsync(NativeProgram program, CancellationToken cancellationToken = default)
    {
        context.AdoptAmbientDirectory();
        program = program with { PreserveLocals = true };
        if (interpreter is null) interpreter = new(program, options, context);
        else interpreter.UpdateProgram(program);
        return interpreter.ExecuteAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => interpreter?.DisposeAsync() ?? ValueTask.CompletedTask;
}
