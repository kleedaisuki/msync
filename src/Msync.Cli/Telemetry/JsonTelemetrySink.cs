using System.Text.Json;

namespace Msync.Cli;

internal sealed class JsonTelemetrySink : ITelemetrySink
{
    private readonly TextWriter writer;
    private readonly bool ownsWriter;
    public JsonTelemetrySink(string path)
    {
        ownsWriter = path != "-";
        writer = ownsWriter ? new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) : Console.Error;
    }
    public void Write(IReadOnlyList<TelemetryRecord> records)
    {
        foreach (TelemetryRecord record in records) writer.WriteLine(JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryRecord));
        writer.Flush();
    }
    public void Complete(string? failure) => writer.Flush();
    public void Dispose() { if (ownsWriter) writer.Dispose(); }
}
