using Msync.Core.Native;
using Msync.Compiler;
using Msync.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private SourceCompletionService? completionService;

    private void BeginCompletion(JsonNode id, JsonElement parameters, EditorDocument document)
    {
        string uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
        var service = completionService ??= new(PromptForSshAsync);
        JsonElement request = parameters.Clone();
        BeginRequest(id, uri, RequestKind.Completion, document,
            async work => await CompletionItemsAsync(work, request, service));
    }

    private async Task<JsonArray> CompletionItemsAsync(AsyncRequest work, JsonElement request, SourceCompletionService service)
    {
        EditorDocument document = work.Document!;
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
        var knownPath = receiver?.KnownPath ?? (receiver?.KnownText is { } text ? new NativePath(text) : null);
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

}
