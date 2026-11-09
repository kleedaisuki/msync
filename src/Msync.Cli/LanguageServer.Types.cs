using System.Text.Json.Nodes;
using Msync.Compiler;
using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static JsonArray NameCompletions(SourceText source, int offset, JsonArray items)
    {
        SourceSpan span = NameSyntax.SpanAt(source.Text, offset);
        foreach (var item in items)
            if (item is JsonObject completion && completion["label"] is { } label)
                completion["textEdit"] = new JsonObject { ["range"] = source.Range(span.Start, span.Length), ["newText"] = label.GetValue<string>() };
        return items;
    }

    private static JsonArray TypeCompletions(SourceText source, int offset, EditorContext? context)
    {
        var selected = TypeCompletion.Find(source.Text, offset);
        SourceSpan span = selected.Span;
        JsonArray items = [];
        foreach (var candidate in TypeCompletion.Candidates(selected, context))
            items.Add((JsonNode)new JsonObject
            {
                ["label"] = candidate.Name,
                ["kind"] = 7,
                ["detail"] = candidate.Signature,
                ["documentation"] = Documentation(candidate.Description),
                ["textEdit"] = new JsonObject { ["range"] = source.Range(span.Start, span.Length), ["newText"] = candidate.Name }
            });
        return items;
    }
}
