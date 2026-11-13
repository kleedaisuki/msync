using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

// The server event loop owns request correlation. Workers only receive an
// immutable parent ID; the shared exporter handles concurrent observations.
internal sealed class LanguageServerObservation(TelemetrySession session)
{
    internal void InputReaderStarted() => session.SetAttribute("lsp.input.reader", "dedicated-thread");

    private sealed record Pending(TelemetrySpan Span, long Received, long Dispatched);
    private readonly Dictionary<string, Pending> requests = new(StringComparer.Ordinal);

    internal void BeginRequest(JsonNode id, string method, JsonElement parameters, long received)
    {
        string key = id.ToJsonString();
        if (requests.ContainsKey(key)) return;
        long dispatched = Stopwatch.GetTimestamp();
        var span = session.Begin("lsp.request")!;
        span.SetAttribute("lsp.method", method);
        span.SetMetric("lsp.queue_ns", Duration(received, dispatched));
        SetDocument(span, Document(parameters));
        requests.Add(key, new(span, received, dispatched));
    }

    internal TelemetrySpan? BeginNotification(string method, JsonElement parameters, long received)
    {
        var span = session.Begin("lsp.notification", TelemetryLevel.Phases);
        if (span is null) return null;
        span.SetAttribute("lsp.method", method);
        span.SetMetric("lsp.queue_ns", TelemetrySession.Elapsed(received));
        SetDocument(span, Document(parameters));
        return span;
    }

    internal void Reply(JsonObject message, string? disposition, int bytes)
    {
        long replied = Stopwatch.GetTimestamp();
        if (message.ContainsKey("method") || message["id"] is not { } id ||
            !requests.Remove(id.ToJsonString(), out var pending)) return;
        using var span = pending.Span;
        span.SetMetric("lsp.response_bytes", bytes);
        span.SetMetric("lsp.dispatch_to_reply_ns", Duration(pending.Dispatched, replied));
        span.SetMetric("lsp.receive_to_reply_ns", Duration(pending.Received, replied));
        if (disposition is not null) span.SetAttribute("lsp.disposition", disposition);
        int? code = message["error"]?["code"]?.GetValue<int>();
        if (code is int errorCode) span.SetAttribute("lsp.error_code", errorCode.ToString(CultureInfo.InvariantCulture));
        if (disposition is not null || code is -32800 or -32801)
            Cancel(span, "The editor request ended without a current result.");
        else
            span.Complete(code is null);
    }

    internal TelemetrySpan? BeginWork(string name, string? parent, string uri, long queued, EditorDocument? document = null)
    {
        var span = session.Begin(name, TelemetryLevel.Phases, parent ?? TelemetrySession.RootId);
        if (span is null) return null;
        span.SetMetric("lsp.queue_ns", TelemetrySession.Elapsed(queued));
        SetDocument(span, uri);
        if (document is not null)
        {
            span.SetAttribute("document.version", document.Version.ToString(CultureInfo.InvariantCulture));
            span.SetMetric("document.generation", document.Generation);
            span.SetMetric("document.utf16_units", document.Source.Text.Length);
        }
        return span;
    }

    internal void FinishRequests()
    {
        foreach (var pending in requests.Values)
        {
            using var span = pending.Span;
            span.SetAttribute("lsp.disposition", "connection_closed");
            Cancel(span, "The language-server connection closed before the reply.");
        }
        requests.Clear();
    }

    internal void ClientMeasurement(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("operation", out var operationValue) || operationValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("outcome", out var outcomeValue) || outcomeValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("durationNanoseconds", out var durationValue) || durationValue.ValueKind != JsonValueKind.Number ||
            !durationValue.TryGetInt64(out long duration) || duration < 0) return;
        string? operation = operationValue.GetString(), outcome = outcomeValue.GetString();
        if (operation is not ("startup" or "artifact" or "stdlibSource" or "hover" or "completion") ||
            outcome is not ("ok" or "error" or "cancelled")) return;
        session.RecordMeasurement("editor.client." + operation, duration, outcome, new() { ["client.outcome"] = outcome });
    }

    private static string? Document(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (parameters.TryGetProperty("textDocument", out var document) && document.ValueKind == JsonValueKind.Object)
            parameters = document;
        return parameters.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String ? uri.GetString() : null;
    }

    private static void SetDocument(TelemetrySpan span, string? candidate)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Scheme is ("file" or "untitled" or "msync-stdlib" or "vscode-remote") && uri.UserInfo.Length == 0)
            span.SetAttribute("document.uri", uri.GetLeftPart(UriPartial.Path));
    }

    private static void Cancel(TelemetrySpan span, string message) => span.Fail(new OperationCanceledException(message));

    private static long Duration(long start, long end) => (long)Stopwatch.GetElapsedTime(start, end).TotalNanoseconds;
}
