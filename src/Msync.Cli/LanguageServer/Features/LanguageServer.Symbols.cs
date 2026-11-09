using System.Text.Json.Nodes;
using Msync.Compiler;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private JsonArray Symbols(string uri, EditorDocument document)
    {
        JsonArray symbols = [];
        var inferred = (document.Analysis?.Occurrences ?? []).ToDictionary(item => item.Span);
        foreach (var declaration in document.Analysis?.Symbols ?? []) Add(declaration, symbols, null);
        return symbols;

        void Add(EditorSymbol declaration, JsonArray target, string? container)
        {
            inferred.TryGetValue(declaration.SelectionSpan, out var occurrence);
            int kind = occurrence?.SemanticRole switch
            {
                "function" => 12,
                "type" => 5,
                _ => declaration.Kind switch { EditorSymbolKind.Function => 12, EditorSymbolKind.Scope => 5, EditorSymbolKind.Field => 8, _ => 13 }
            };
            JsonObject symbol = new() { ["name"] = declaration.Name, ["kind"] = kind };
            if (hierarchicalDocumentSymbols)
            {
                symbol["range"] = document.Source.Range(declaration.Span.Start, declaration.Span.Length);
                symbol["selectionRange"] = document.Source.Range(declaration.SelectionSpan.Start, declaration.SelectionSpan.Length);
                if (occurrence is not null) symbol["detail"] = occurrence.Signature;
                if (declaration.Children.Count != 0)
                {
                    JsonArray children = [];
                    foreach (var child in declaration.Children) Add(child, children, declaration.Name);
                    symbol["children"] = children;
                }
                target.Add((JsonNode)symbol);
                return;
            }
            symbol["location"] = new JsonObject
            {
                ["uri"] = uri,
                ["range"] = document.Source.Range(declaration.SelectionSpan.Start, declaration.SelectionSpan.Length)
            };
            if (container is not null) symbol["containerName"] = container;
            target.Add((JsonNode)symbol);
            foreach (var child in declaration.Children) Add(child, target, declaration.Name);
        }
    }
}
