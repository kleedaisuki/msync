namespace Msync.Cli;

internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource source = new();
    internal ConsoleCancellation() => Console.CancelKeyPress += Cancel;
    internal CancellationToken Token => source.Token;

    private void Cancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= Cancel;
        source.Dispose();
    }
}
