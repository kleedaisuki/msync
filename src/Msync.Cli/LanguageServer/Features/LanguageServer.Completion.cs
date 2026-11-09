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
            result = await CompletionItemsAsync(work, request, service);
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or MsyncException)
        { error = failure; }
        if (!events.Writer.TryWrite(new CompletionEvent(work, result, error))) work.Cancellation.Dispose();
    }

    private async Task<JsonArray> CompletionItemsAsync(CompletionWork work, JsonElement request, SourceCompletionService service)
    {
        EditorDocument document = work.Document;
        int offset = Offset(document.Source, request.GetProperty("position"));
        var insight = document.Analysis?.At(offset);
        if (insight?.IsGlobalImport == true || insight?.IsTypePosition == true)
            return Completions(document, request, insight, completionResolveDocumentation);
        string? sourceFile = Uri.TryCreate(work.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        JsonArray? items = null;
        var builtin = BuiltinCompletion.Find(document.Source.Text, offset, insight);
        if (builtin is not null)
        {
            items = BuiltinItems(document.Source, builtin, BuiltinCompletion.Complete(builtin, Environment.CurrentDirectory, sourceFile));
            if (!BuiltinCompletion.IncludesDirectoryEntries(builtin)) return items;
        }
        var receiver = insight?.Receiver;
        var knownPath = receiver?.KnownPath ?? (receiver?.KnownText is { } text ? new PathValue(text) : null);
        var context = SourceCompletion.Find(document.Source.Text, offset,
            receiverPath: knownPath,
            unknownPathReceiver: (receiver?.Type is "Path" or "Path?" or "String" or "String?" or "unknown") && knownPath is null,
            physicalEndpoint: insight?.PhysicalEndpoint,
            unknownPhysicalOwner: insight?.HasPhysicalOwner == true && insight.PhysicalEndpoint is null,
            boundNames: insight?.Bindings.Where(binding => !binding.IsConstraint).Select(binding => binding.Name).ToHashSet(StringComparer.Ordinal),
            sourceFile: sourceFile);
        if (context is null)
            return items ?? (PromptSyntax.InsideLiteral(document.Source.Text, offset) ? [] : Completions(document, request, insight, completionResolveDocumentation));
        return SourceItems(document.Source, context,
            await service.CompleteAsync(context, Environment.CurrentDirectory, HostContext.HomeDirectory, work.Cancellation.Token), items);
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
        transport.Write(reply);
    }
}
