using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private sealed record EditorDocument(SourceText Source, int Version, long Generation)
    {
        public EditorDocument(string text, int version, long generation) : this(new SourceText(text), version, generation) { }
    }
    private abstract record ServerEvent;
    private sealed record MessageEvent(JsonDocument Message) : ServerEvent;
    private sealed record InputClosed : ServerEvent;
    private sealed record InputError(Exception Error) : ServerEvent;
    private sealed record AnalysisEvent(string Uri, EditorDocument Document, EditorDiagnostic? Diagnostic) : ServerEvent;
    private sealed record AnalysisWork(string Uri, EditorDocument Document);

    private readonly Channel<ServerEvent> events = Channel.CreateUnbounded<ServerEvent>(new() { SingleReader = true });
    private readonly Channel<string> analysisReady = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
    private readonly Dictionary<string, AnalysisWork> pendingAnalysis = new(StringComparer.Ordinal);
    private readonly object analysisGate = new();
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
            events.Writer.TryComplete();
            while (events.Reader.TryRead(out ServerEvent? item))
                if (item is MessageEvent incoming) incoming.Message.Dispose();
        }
    }

    private void ReadMessages()
    {
        try
        {
            while (Read() is { } message)
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
        string method = root.TryGetProperty("method", out JsonElement name) ? name.GetString() ?? "" : "";
        JsonNode? id = root.TryGetProperty("id", out JsonElement requestId) ? JsonNode.Parse(requestId.GetRawText()) : null;
        JsonElement parameters = root.TryGetProperty("params", out JsonElement value) ? value : default;
        if (method == "exit") return true;
        if (method == "shutdown") StopAnalysis();
        try
        {
            if (id is not null) Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(method, parameters) });
            else if (!shuttingDown) Notify(method, parameters);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            if (id is not null) Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32602, ["message"] = error.Message } });
            else Console.Error.WriteLine($"LSP {method}: {error.Message}");
        }
        return false;
    }

    private void Notify(string method, JsonElement parameters)
    {
        if (method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")) return;
        JsonElement descriptor = parameters.GetProperty("textDocument");
        string uri = descriptor.GetProperty("uri").GetString()!;
        if (method == "textDocument/didClose")
        {
            documents.Remove(uri);
            lock (analysisGate) pendingAnalysis.Remove(uri);
            PublishDiagnostics(uri, null, []);
            return;
        }
        int version = descriptor.GetProperty("version").GetInt32();
        EditorDocument? previous = documents.GetValueOrDefault(uri);
        if (method == "textDocument/didChange" && previous is not null && version <= previous.Version) return;
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
            EditorDiagnostic? diagnostic = check(work.Document.Source.Text, uri);
            events.Writer.TryWrite(new AnalysisEvent(uri, work.Document, diagnostic));
        }
    }

    private void PublishAnalysis(AnalysisEvent completed)
    {
        if (shuttingDown || !documents.TryGetValue(completed.Uri, out EditorDocument? current) || current.Generation != completed.Document.Generation) return;
        JsonArray diagnostics = [];
        if (completed.Diagnostic is { } diagnostic)
        {
            JsonObject range = diagnostic.Offset is { } offset ? current.Source.Range(offset, diagnostic.Length)
                : new() { ["start"] = Position(Math.Max(0, diagnostic.Line - 1), Math.Max(0, diagnostic.Column - 1)), ["end"] = Position(Math.Max(0, diagnostic.Line - 1), Math.Max(0, diagnostic.Column)) };
            diagnostics.Add((JsonNode)new JsonObject { ["range"] = range, ["severity"] = 1, ["source"] = "msync", ["message"] = diagnostic.Message });
        }
        PublishDiagnostics(completed.Uri, current.Version, diagnostics);
    }

    private void PublishDiagnostics(string uri, int? version, JsonArray diagnostics) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = uri, ["version"] = version, ["diagnostics"] = diagnostics } });

    private void StopAnalysis()
    {
        shuttingDown = true;
        lock (analysisGate) pendingAnalysis.Clear();
        analysisReady.Writer.TryComplete();
    }

    private static int Offset(SourceText source, JsonElement position) =>
        source.Offset(position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());
}
