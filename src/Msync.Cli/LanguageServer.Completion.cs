using Msync.Compiler;
using Msync.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private sealed record CompletionWork(JsonNode Id, string Uri, EditorDocument Document, CancellationTokenSource Cancellation);
    private sealed record CompletionEvent(CompletionWork Work, JsonNode? Result, Exception? Error) : ServerEvent;
    private readonly Dictionary<string, CompletionWork> completionRequests = new(StringComparer.Ordinal);
    private SourceCompletionService? completionService;

    private void BeginCompletion(JsonNode id, JsonElement parameters, EditorDocument document)
    {
        string uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
        CompletionWork work = new(id, uri, document, new());
        completionRequests.Add(id.ToJsonString(), work);
        var service = completionService ??= new(PromptForSshAsync);
        JsonElement request = parameters.Clone();
        _ = Task.Run(() => ComputeCompletionAsync(work, request, service));
    }

    private async Task ComputeCompletionAsync(CompletionWork work, JsonElement request, SourceCompletionService service)
    {
        JsonNode? result = null;
        Exception? error = null;
        try
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            EditorDocument document = work.Document;
            int offset = Offset(document.Source, request.GetProperty("position"));
            var editorContext = document.Analysis?.At(offset);
            var receiver = editorContext?.Receiver;
            var knownPath = receiver?.KnownPath ?? (receiver?.KnownText is { } text ? new PathValue(text) : null);
            var context = SourceCompletion.Find(document.Source.Text, offset,
                resolveEndpoint: name => CompletionEndpoint(editorContext, name),
                receiverPath: knownPath,
                unknownPathReceiver: (receiver?.Type is "Path" or "Path?" or "String" or "String?" or "unknown") && knownPath is null,
                physicalEndpoint: editorContext?.PhysicalEndpoint,
                unknownPhysicalOwner: editorContext?.HasPhysicalOwner == true && editorContext.PhysicalEndpoint is null);
            result = context is null ? Completions(document, request, editorContext, completionResolveDocumentation)
                : SourceItems(document.Source, context,
                    await service.CompleteAsync(context, Environment.CurrentDirectory, HostContext.HomeDirectory, work.Cancellation.Token));
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or MsyncException)
        { error = failure; }
        if (!events.Writer.TryWrite(new CompletionEvent(work, result, error))) work.Cancellation.Dispose();
    }

    private static ConnectionValue? CompletionEndpoint(EditorContext? context, string name)
    {
        if (context?.Receiver?.Endpoint is { } endpoint) return endpoint;
        IReadOnlyList<SemanticMember>? members = context?.Bindings;
        SemanticMember? selected = null;
        foreach (string component in name.Split("::", StringSplitOptions.TrimEntries))
        {
            selected = members?.FirstOrDefault(member => member.Name == component);
            if (selected is null) return null;
            members = selected.Members;
        }
        return selected?.Endpoint;
    }

    private void PublishCompletion(CompletionEvent completed)
    {
        var work = completed.Work;
        work.Cancellation.Dispose();
        if (!completionRequests.Remove(work.Id.ToJsonString())) return;
        if (shuttingDown || !documents.TryGetValue(work.Uri, out var current) || current.Generation != work.Document.Generation)
        {
            CompletionReply(work, null, -32801, "The document changed while completion was running.");
            return;
        }
        if (completed.Error is OperationCanceledException) CompletionReply(work, null, -32800, "Completion cancelled.");
        else if (completed.Error is { } error) CompletionReply(work, null, -32603, error.Message);
        else CompletionReply(work, completed.Result);
    }

    private void CancelCompletions(string? uri = null, string? id = null)
    {
        foreach (var (key, work) in completionRequests.ToArray())
        {
            if (uri is not null && work.Uri != uri || id is not null && key != id) continue;
            completionRequests.Remove(key);
            work.Cancellation.Cancel();
            CompletionReply(work, null, -32800, "Completion cancelled.");
        }
    }

    private void CompletionReply(CompletionWork work, JsonNode? result, int? code = null, string? message = null)
    {
        JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = work.Id };
        if (code is { } number) reply["error"] = new JsonObject { ["code"] = number, ["message"] = message };
        else reply["result"] = result;
        Write(reply);
    }
}
