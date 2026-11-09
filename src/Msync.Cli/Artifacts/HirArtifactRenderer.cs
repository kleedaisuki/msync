using System.Text.Json.Nodes;
using Msync.Hir;

namespace Msync.Cli;

internal static class HirArtifactRenderer
{
    internal static ArtifactDisassemblyResult Read(byte[] bytes, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new(bytes, writable: false);
        HirModule module = HirArtifact.Read(stream);
        cancellationToken.ThrowIfCancellationRequested();
        HirPrintedModule printed = HirPrinter.Describe(module, cancellationToken);
        JsonArray symbols = [], references = [], hovers = [];
        foreach (var definition in printed.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject range = Range(definition.Line, definition.Character, definition.Length);
            symbols.Add((JsonNode)new JsonObject
            {
                ["name"] = definition.Name + " (" + definition.Identifier + ")",
                ["detail"] = definition.Description,
                ["kind"] = SymbolKind(definition.Category),
                ["range"] = range.DeepClone(),
                ["selectionRange"] = range.DeepClone()
            });
            hovers.Add((JsonNode)new JsonObject
            {
                ["range"] = range.DeepClone(),
                ["markdown"] = $"**{definition.Identifier}** · {definition.Category}\n\n{definition.Description}"
            });
            if (definition.Source is { } source && source.Source.Value >= 0)
            {
                string name = module.Sources[source.Source.Value].Name;
                JsonObject reference = new()
                {
                    ["range"] = range.DeepClone(),
                    ["sourceName"] = name,
                    ["sourceStart"] = source.Start,
                    ["sourceLength"] = source.Length
                };
                if (SourceUri(name) is { } uri) reference["sourceUri"] = uri;
                references.Add((JsonNode)reference);
            }
        }
        foreach (var reference in printed.References)
            references.Add((JsonNode)new JsonObject
            {
                ["range"] = Range(reference.Line, reference.Character, reference.Length),
                ["targetLine"] = reference.TargetLine,
                ["targetCharacter"] = reference.TargetCharacter
            });
        return new(printed.Text, HirArtifact.FormatVersion, "typedhir", null, symbols, references, hovers, "hir");
    }

    private static int SymbolKind(HirTextCategory category) => category switch
    {
        HirTextCategory.Function or HirTextCategory.Template => 12,
        HirTextCategory.Type or HirTextCategory.Shape or HirTextCategory.Concept => 5,
        HirTextCategory.Location => 13,
        HirTextCategory.Literal => 14,
        HirTextCategory.Source => 1,
        _ => 3
    };

    private static string? SourceUri(string name)
    {
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri)
            && (uri.IsFile || uri.Scheme is "msync-stdlib" or "vscode-remote")) return uri.AbsoluteUri;
        if (name.StartsWith('<') && name.EndsWith('>')) return null;
        return Path.IsPathFullyQualified(name) ? new Uri(name).AbsoluteUri : null;
    }

    private static JsonObject Range(int line, int character, int length) => new()
    {
        ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
        ["end"] = new JsonObject { ["line"] = line, ["character"] = character + length }
    };
}
