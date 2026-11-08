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

    private static JsonArray TypeCompletions(SourceText source, int offset)
    {
        SourceSpan span = NameSyntax.SpanAt(source.Text, offset);
        JsonArray items = [];
        foreach (string name in LanguageTypes.Names)
            items.Add((JsonNode)new JsonObject
            {
                ["label"] = name,
                ["kind"] = 7,
                ["detail"] = name == "Range" ? "Range<T> — constrain the element type" : $"{name} — type constraint",
                ["textEdit"] = new JsonObject { ["range"] = source.Range(span.Start, span.Length), ["newText"] = name }
            });
        return items;
    }
}
