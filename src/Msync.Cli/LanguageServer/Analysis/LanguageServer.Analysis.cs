using Msync.Compiler;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private abstract record ServerEvent;
    private sealed record MessageEvent(JsonDocument Message) : ServerEvent;
    private sealed record InputClosed : ServerEvent;
    private sealed record InputError(Exception Error) : ServerEvent;
    private sealed record AnalysisEvent(string Uri, EditorDocument Document, EditorAnalysis Analysis) : ServerEvent;
    private sealed record AnalysisWork(string Uri, EditorDocument Document);
    private sealed record PendingInsight(string Uri, long Generation, JsonNode Id, string Method, JsonElement Parameters);

    private readonly Channel<ServerEvent> events = Channel.CreateUnbounded<ServerEvent>(new() { SingleReader = true });
    private readonly Channel<string> analysisReady = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
    private readonly Dictionary<string, AnalysisWork> pendingAnalysis = new(StringComparer.Ordinal);
    private readonly object analysisGate = new();
    private readonly List<PendingInsight> pendingInsights = [];
    private long generation;
    private bool shuttingDown;

    public async Task<int> RunAsync()
    {
        _ = Task.Run(ReadMessages);
        _ = Task.Run(AnalyzeAsync);
        try
        {
            await foreach (ServerEvent item in events.Reader.ReadAllAsync())
            {
                switch (item)
                {
                    case InputClosed: return 0;
                    case InputError failure: Console.Error.WriteLine($"LSP input: {failure.Error.Message}"); return 1;
                    case AnalysisEvent completed: PublishAnalysis(completed); break;
                    case CompletionEvent completed: PublishCompletion(completed); break;
                    case ArtifactEvent completed: PublishArtifact(completed); break;
                    case AuthEvent authentication: HandleAuthentication(authentication); break;
                    case MessageEvent incoming:
                        using (incoming.Message)
                            if (Dispatch(incoming.Message.RootElement)) return 0;
                        break;
                }
            }
            return 0;
        }
        finally
        {
            StopAnalysis();
            if (completionService is not null) await completionService.DisposeAsync();
            events.Writer.TryComplete();
            while (events.Reader.TryRead(out ServerEvent? item))
                if (item is MessageEvent incoming) incoming.Message.Dispose();
                else if (item is CompletionEvent completed) completed.Work.Cancellation.Dispose();
                else if (item is ArtifactEvent artifactDone) artifactDone.Work.Cancellation.Dispose();
        }
    }

    private void ReadMessages()
    {
        try
        {
            while (transport.Read() is { } message)
                if (!events.Writer.TryWrite(new MessageEvent(message))) { message.Dispose(); return; }
            events.Writer.TryWrite(new InputClosed());
        }
        catch (Exception error) when (error is IOException or JsonException or FormatException or ArgumentException)
        {
            events.Writer.TryWrite(new InputError(error));
        }
    }

    private bool Dispatch(JsonElement root)
    {
        if (!root.TryGetProperty("method", out _))
        {
            if (HandleAuthenticationResponse(root)) return false;
            if (root.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.String
                && responseId.GetString() == $"msync-refresh-{serverRequest}")
            {
                semanticRefreshPending = false;
                RefreshSemanticTokens();
            }
            return false;
        }
        string method = root.TryGetProperty("method", out JsonElement name) ? name.GetString() ?? "" : "";
        JsonNode? id = root.TryGetProperty("id", out JsonElement requestId) ? JsonNode.Parse(requestId.GetRawText()) : null;
        JsonElement parameters = root.TryGetProperty("params", out JsonElement value) ? value : default;
        if (method == "exit") return true;
        if (method == "shutdown") StopAnalysis();
        try
        {
            if (id is not null && method == "msync/artifact")
            {
                BeginArtifact(id, parameters);
                return false;
            }
            if (id is not null && (method is "textDocument/hover" or "textDocument/semanticTokens/full" or "textDocument/completion"
                or "textDocument/documentSymbol" or "textDocument/definition")
                && parameters.TryGetProperty("textDocument", out var descriptor)
                && documents.TryGetValue(descriptor.GetProperty("uri").GetString()!, out var document)
                && document.Analysis is null)
            {
                pendingInsights.Add(new(descriptor.GetProperty("uri").GetString()!, document.Generation, id, method, parameters.Clone()));
                return false;
            }
            if (id is not null && method == "textDocument/completion"
                && documents.TryGetValue(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!, out var completionDocument))
            {
                BeginCompletion(id, parameters, completionDocument);
                return false;
            }
            if (id is not null) transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(method, parameters) });
            else if (!shuttingDown) Notify(method, parameters);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            if (id is not null) transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32602, ["message"] = error.Message } });
            else Console.Error.WriteLine($"LSP {method}: {error.Message}");
        }
        return false;
    }

    private void Notify(string method, JsonElement parameters)
    {
        if (method == "$/cancelRequest")
        {
            string requested = parameters.GetProperty("id").GetRawText();
            CancelCompletions(id: requested);
            CancelArtifacts(id: requested);
            for (int index = pendingInsights.Count - 1; index >= 0; index--)
                if (pendingInsights[index].Id.ToJsonString() == requested)
                {
                    transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = pendingInsights[index].Id, ["error"] = new JsonObject { ["code"] = -32800, ["message"] = "Editor request cancelled." } });
                    pendingInsights.RemoveAt(index);
                }
            return;
        }
        if (method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")) return;
        JsonElement descriptor = parameters.GetProperty("textDocument");
        string uri = descriptor.GetProperty("uri").GetString()!;
        if (method == "textDocument/didClose")
        {
            CancelCompletions(uri);
            CompleteInsights(uri);
            documents.Remove(uri);
            lock (analysisGate) pendingAnalysis.Remove(uri);
            PublishDiagnostics(uri, null, []);
            return;
        }
        int version = descriptor.GetProperty("version").GetInt32();
        EditorDocument? previous = documents.GetValueOrDefault(uri);
        if (method == "textDocument/didChange" && previous is not null && version <= previous.Version) return;
        CancelCompletions(uri);
        CompleteInsights(uri);
        SourceText source = method == "textDocument/didOpen" ? new(descriptor.GetProperty("text").GetString()!) : ApplyChanges(previous?.Source ?? new(""), parameters);
        EditorDocument current = new(source, version, ++generation);
        documents[uri] = current;
        lock (analysisGate)
        {
            if (!pendingAnalysis.ContainsKey(uri)) analysisReady.Writer.TryWrite(uri);
            pendingAnalysis[uri] = new(uri, current);
        }
    }

    private static SourceText ApplyChanges(SourceText source, JsonElement parameters)
    {
        foreach (JsonElement change in parameters.GetProperty("contentChanges").EnumerateArray())
        {
            string replacement = change.GetProperty("text").GetString()!;
            if (!change.TryGetProperty("range", out JsonElement range)) { source = new(replacement); continue; }
            int start = Offset(source, range.GetProperty("start"));
            int end = Offset(source, range.GetProperty("end"));
            source = new(string.Concat(source.Text.AsSpan(0, start), replacement, source.Text.AsSpan(end)));
        }
        return source;
    }

    private async Task AnalyzeAsync()
    {
        await foreach (string uri in analysisReady.Reader.ReadAllAsync())
        {
            AnalysisWork? work;
            lock (analysisGate)
                if (!pendingAnalysis.Remove(uri, out work)) continue;
            EditorAnalysis analysis = check(work.Document.Source.Text, uri);
            events.Writer.TryWrite(new AnalysisEvent(uri, work.Document, analysis));
        }
    }

    private void PublishAnalysis(AnalysisEvent completed)
    {
        if (shuttingDown || !documents.TryGetValue(completed.Uri, out EditorDocument? current) || current.Generation != completed.Document.Generation) return;
        current.Analysis = completed.Analysis;
        CompleteInsights(completed.Uri, current);
        JsonArray diagnostics = [];
        if (completed.Analysis.Diagnostic is { } diagnostic)
        {
            DiagnosticLocation primary = new(diagnostic.SourceName, diagnostic.Source, diagnostic.Span, diagnostic.Label ?? "here");
            var locations = diagnostic.Related.AsEnumerable();
            if (diagnostic.SourceName != completed.Uri)
            {
                var call = diagnostic.Related.FirstOrDefault(location => location.SourceName == completed.Uri);
                if (call is not null)
                {
                    primary = call;
                    locations = locations.Where(location => location != call).Prepend(new(diagnostic.SourceName, diagnostic.Source, diagnostic.Span, diagnostic.Message));
                }
            }
            JsonArray related = [];
            foreach (var location in locations)
                related.Add((JsonNode)new JsonObject
                {
                    ["location"] = new JsonObject { ["uri"] = DocumentUri(location.SourceName), ["range"] = new SourceText(location.Source).Range(location.Span.Start, location.Span.Length) },
                    ["message"] = location.Message
                });
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["range"] = current.Source.Range(primary.Span.Start, primary.Span.Length),
                ["severity"] = 1,
                ["code"] = diagnostic.Code,
                ["source"] = "msync",
                ["message"] = diagnostic.EditorMessage,
                ["relatedInformation"] = related
            });
        }
        PublishDiagnostics(completed.Uri, current.Version, diagnostics);
        semanticRefreshDirty = true;
        RefreshSemanticTokens();
    }

    private void RefreshSemanticTokens()
    {
        if (!semanticRefreshSupported || semanticRefreshPending || !semanticRefreshDirty || shuttingDown) return;
        semanticRefreshDirty = false;
        semanticRefreshPending = true;
        transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = $"msync-refresh-{++serverRequest}", ["method"] = "workspace/semanticTokens/refresh" });
    }

    private static string DocumentUri(string sourceName) =>
        Uri.TryCreate(sourceName, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : new Uri(Path.GetFullPath(sourceName)).AbsoluteUri;

    private void PublishDiagnostics(string uri, int? version, JsonArray diagnostics) =>
        transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = uri, ["version"] = version, ["diagnostics"] = diagnostics } });

    private void StopAnalysis()
    {
        shuttingDown = true;
        CancelCompletions();
        CancelArtifacts();
        StopAuthentication();
        foreach (var request in pendingInsights)
            transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id, ["result"] = null });
        pendingInsights.Clear();
        lock (analysisGate) pendingAnalysis.Clear();
        analysisReady.Writer.TryComplete();
    }

    private void CompleteInsights(string uri, EditorDocument? document = null)
    {
        for (int index = pendingInsights.Count - 1; index >= 0; index--)
        {
            var request = pendingInsights[index];
            if (request.Uri != uri) continue;
            pendingInsights.RemoveAt(index);
            if (document?.Generation == request.Generation && request.Method == "textDocument/completion")
            {
                BeginCompletion(request.Id, request.Parameters, document);
                continue;
            }
            JsonNode? result = document?.Generation == request.Generation ? Handle(request.Method, request.Parameters) : null;
            transport.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id, ["result"] = result });
        }
    }

    private static int Offset(SourceText source, JsonElement position) =>
        source.Offset(position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());
}
