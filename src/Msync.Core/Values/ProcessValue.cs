namespace Msync.Core;

public sealed record ProcessValue(int ExitCode, string StandardOutput, string StandardError) : Value
{
    public Action? AbortOwnedTransport { get; init; }
    public Task<ProcessValue>? Completion { get; init; }
    public IProcessControl? Control { get; init; }
    public int? ProcessId { get; init; }
    public override TypeRef Type => TypeRef.Process;
    public override string ToString() => $"Process(exit={ExitCode})";
}

public interface IProcessControl
{
    int ProcessId { get; }
    bool IsRunning { get; }
    ValueTask WriteInputAsync(string text, CancellationToken cancellationToken);
    ValueTask CloseInputAsync(CancellationToken cancellationToken);
    bool Kill();
    bool Terminate();
}
