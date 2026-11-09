using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static JsonArray BuiltinItems(SourceText source, BuiltinCompletionContext context, IReadOnlyList<SourceCompletionItem> candidates)
    {
        JsonArray items = [];
        foreach (var candidate in candidates)
            items.Add((JsonNode)new JsonObject
            {
                ["label"] = candidate.Label,
                ["kind"] = context.Kind == BuiltinCompletionKind.Root ? 9 : candidate.IsDirectory ? 19
                    : context.Kind is BuiltinCompletionKind.SearchPath or BuiltinCompletionKind.SourceFile ? 17 : 6,
                ["detail"] = candidate.Detail.Split('\n', 2)[0],
                ["documentation"] = Documentation(candidate.Detail),
                ["filterText"] = candidate.FilterText ?? candidate.Label,
                ["textEdit"] = new JsonObject { ["range"] = source.Range(context.Start, context.Length), ["newText"] = candidate.Replacement }
            });
        return items;
    }
}
