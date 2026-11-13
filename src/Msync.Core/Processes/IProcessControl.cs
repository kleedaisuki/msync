namespace Msync.Core;

public interface IProcessControl
{
    int ProcessId { get; }
    bool IsRunning { get; }
    ValueTask WriteInputAsync(string text, CancellationToken cancellationToken);
    ValueTask CloseInputAsync(CancellationToken cancellationToken);
    bool Kill();
    bool Terminate();
}
