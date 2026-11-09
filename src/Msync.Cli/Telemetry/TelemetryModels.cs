using System.Text.Json.Serialization;

namespace Msync.Cli;

internal sealed record TelemetryCause(string Message, string? Kind = null, int? RawOsError = null);
internal sealed record TelemetryRecord(string Kind, string TraceId, string SpanId, string? ParentSpanId, string Name,
    long? StartUnixNs, long? DurationNs, long? TimestampUnixNs, string? Status, string? Completion,
    Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics, TelemetryCause[] ErrorChain)
{
    public int SchemaVersion => 1;
    public bool ErrorChainTruncated => false;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelemetryRecord))]
internal partial class TelemetryJsonContext : JsonSerializerContext;
