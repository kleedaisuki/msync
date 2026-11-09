using System.Text.Json;
using System.Text.Json.Nodes;
using Msync.Core;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private sealed record ArtifactWork(JsonNode Id, string Uri, string Data, CancellationTokenSource Cancellation);
    private sealed record ArtifactEvent(ArtifactWork Work, JsonObject? Result, Exception? Error) : ServerEvent;
    private readonly Dictionary<string, ArtifactWork> artifactRequests = new(StringComparer.Ordinal);

    private void BeginArtifact(JsonNode id, JsonElement parameters)
    {
        string uri = parameters.GetProperty("uri").GetString()
            ?? throw new ArgumentException("The artifact source URI is required.");
        string data = parameters.GetProperty("data").GetString()
            ?? throw new ArgumentException("The artifact byte payload is required.");
        if (shuttingDown)
        {
            ArtifactReply(id, null, -32800, "The language server is shutting down.");
            return;
        }
        CancelArtifacts(uri: uri);
        ArtifactWork work = new(id, uri, data, new());
        artifactRequests.Add(id.ToJsonString(), work);
        _ = Task.Run(() => ComputeArtifact(work));
    }

    private void ComputeArtifact(ArtifactWork work)
    {
        JsonObject? result = null;
        Exception? error = null;
        try
        {
            var cancellation = work.Cancellation.Token;
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes = Convert.FromBase64String(work.Data);
            cancellation.ThrowIfCancellationRequested();
            result = ArtifactDisassembly.Read(bytes, work.Uri, cancellation).ToJson();
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException or MsyncException)
        { error = failure; }
        if (!events.Writer.TryWrite(new ArtifactEvent(work, result, error))) work.Cancellation.Dispose();
    }

    private void PublishArtifact(ArtifactEvent completed)
    {
        var work = completed.Work;
        work.Cancellation.Dispose();
        if (!artifactRequests.Remove(work.Id.ToJsonString())) return;
        if (completed.Error is OperationCanceledException) ArtifactReply(work.Id, null, -32800, "Artifact inspection cancelled.");
        else if (completed.Error is { } error) ArtifactReply(work.Id, null, -32001, $"Cannot inspect bytecode: {error.Message}");
        else ArtifactReply(work.Id, completed.Result);
    }

    private void CancelArtifacts(string? uri = null, string? id = null)
    {
        foreach (var (key, work) in artifactRequests.ToArray())
        {
            if (uri is not null && work.Uri != uri || id is not null && key != id) continue;
            artifactRequests.Remove(key);
            work.Cancellation.Cancel();
            ArtifactReply(work.Id, null, -32800, "Artifact inspection cancelled.");
        }
    }

    private void ArtifactReply(JsonNode id, JsonObject? result, int? code = null, string? message = null)
    {
        JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id };
        if (code is { } value) reply["error"] = new JsonObject { ["code"] = value, ["message"] = message };
        else reply["result"] = result;
        transport.Write(reply);
    }
}
