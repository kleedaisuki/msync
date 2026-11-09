using Msync.Core;

namespace Msync.Runtime;

public sealed class ExecutionSession(ExecutionOptions? options = null) : IAsyncDisposable
{
    private sealed record UninitializedValue : Value { public override TypeRef Type => new(TypeKind.Never); }
    private static readonly Value Uninitialized = new UninitializedValue();
    private Value[] locals = [];
    private SessionImage? image;
    private Interpreter? interpreter;
    private string workingDirectory = Path.GetFullPath(options?.WorkingDirectory ?? Environment.CurrentDirectory);
    public string WorkingDirectory => workingDirectory;
    public void ChangeWorkingDirectory(string path)
    {
        RuntimeContext context = new((options ?? new()) with { WorkingDirectory = workingDirectory,
            WorkingDirectoryChanged = AcceptWorkingDirectory });
        context.ChangeWorkingDirectory(new StringValue(path));
    }
    private void AcceptWorkingDirectory(string directory)
    {
        workingDirectory = directory;
        options?.WorkingDirectoryChanged?.Invoke(directory);
    }
    private Value ReadSessionLocal(int slot) => locals[slot] is CellValue cell ? cell.Data : locals[slot];
    public bool IsLocalInitialized(int slot) => (uint)slot < (uint)locals.Length && !ReferenceEquals(ReadSessionLocal(slot), Uninitialized);
    public Value? GetLocalValue(int slot) => IsLocalInitialized(slot) ? ReadSessionLocal(slot) : null;
    public ValueTask<Value> ExecuteAsync(Module module, int startInstruction = 0, CancellationToken cancellationToken = default)
    {
        var count = module.Functions[module.EntryPoint].LocalCount;
        var previousLength = locals.Length; Array.Resize(ref locals, count);
        for (var i = previousLength; i < count; i++) locals[i] = Uninitialized;
        module = module with { PreserveLocals = true };
        if (image is null) image = new(module);
        else image.Publish(module);
        interpreter = new Interpreter(module, (options ?? new()) with
        { WorkingDirectory = workingDirectory, WorkingDirectoryChanged = AcceptWorkingDirectory }, interpreter);
        interpreter.Attach(image);
        return interpreter.ExecuteEntryAsync(locals, startInstruction, cancellationToken);
    }
    public ValueTask DisposeAsync() => interpreter?.CloseSessionAsync() ?? ValueTask.CompletedTask;
}
