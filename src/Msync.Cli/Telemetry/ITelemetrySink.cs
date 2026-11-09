namespace Msync.Cli;

internal interface ITelemetrySink : IDisposable
{
    void Write(IReadOnlyList<TelemetryRecord> records);
    void Complete(string? failure);
}
