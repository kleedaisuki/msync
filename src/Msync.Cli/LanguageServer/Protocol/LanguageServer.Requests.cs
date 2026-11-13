using System.Text.Json.Nodes;
using Msync.Core;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private enum RequestKind { Completion, Artifact }
    private sealed record AsyncRequest(JsonNode Id, string Uri, RequestKind Kind, EditorDocument? Document,
        CancellationTokenSource Cancellation);
    private sealed record RequestEvent(AsyncRequest Work, JsonNode? Result, Exception? Error) : ServerEvent;
    private readonly Dictionary<string, AsyncRequest> requests = new(StringComparer.Ordinal);

    private void BeginRequest(JsonNode id, string uri, RequestKind kind, EditorDocument? document,
        Func<AsyncRequest, Task<JsonNode?>> compute)
    {
        AsyncRequest work = new(id, uri, kind, document, new());
        requests.Add(id.ToJsonString(), work);
        _ = Task.Run(async () =>
        {
            JsonNode? result = null;
            Exception? error = null;
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                result = await compute(work);
                work.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or FormatException or OverflowException
                or System.ComponentModel.Win32Exception or MsyncException)
            { error = failure; }
            if (!events.Writer.TryWrite(new RequestEvent(work, result, error))) work.Cancellation.Dispose();
        });
    }

    private void PublishRequest(RequestEvent completed)
    {
        AsyncRequest work = completed.Work;
        work.Cancellation.Dispose();
        if (!requests.Remove(work.Id.ToJsonString())) return;
        if (work.Kind == RequestKind.Completion && (shuttingDown || !documents.TryGetValue(work.Uri, out var current)
            || current.Generation != work.Document!.Generation))
            Respond(work.Id, code: -32801, message: "The document changed while completion was running.");
        else if (completed.Error is OperationCanceledException) Cancelled(work);
        else if (completed.Error is { } error)
            Respond(work.Id, code: work.Kind == RequestKind.Artifact ? -32001 : -32603,
                message: work.Kind == RequestKind.Artifact ? $"Cannot inspect artifact: {error.Message}" : error.Message);
        else Respond(work.Id, completed.Result);
    }

    private void CancelRequests(RequestKind? kind = null, string? uri = null, string? id = null)
    {
        foreach (var (key, work) in requests.ToArray())
        {
            if (kind is not null && work.Kind != kind || uri is not null && work.Uri != uri || id is not null && key != id) continue;
            requests.Remove(key);
            work.Cancellation.Cancel();
            Cancelled(work);
        }
    }

    private void Cancelled(AsyncRequest work) => Respond(work.Id, code: -32800,
        message: work.Kind == RequestKind.Artifact ? "Artifact inspection cancelled." : "Completion cancelled.");

    private void Respond(JsonNode id, JsonNode? result = null, int? code = null, string? message = null)
    {
        JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id };
        if (code is { } value) reply["error"] = new JsonObject { ["code"] = value, ["message"] = message };
        else reply["result"] = result;
        Reply(reply);
    }
}
