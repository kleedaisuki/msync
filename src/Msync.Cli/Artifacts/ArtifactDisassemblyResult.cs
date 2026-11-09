using System.Text.Json.Nodes;
using Msync.Core;

namespace Msync.Cli;

internal sealed record ArtifactDisassemblyResult(string Text, int FormatVersion, string Stage,
    int? OptimizationLevel, JsonArray Symbols, JsonArray References, JsonArray Hovers,
    string ArtifactKind = "bytecode")
{
    internal JsonObject ToJson()
    {
        JsonObject result = new()
        {
            ["schemaVersion"] = 1,
            ["formatVersion"] = FormatVersion,
            ["languageVersion"] = Artifact.LanguageVersion,
            ["stage"] = Stage,
            ["optimizationLevel"] = OptimizationLevel,
            ["text"] = Text,
            ["symbols"] = Symbols,
            ["references"] = References,
            ["hovers"] = Hovers
        };
        if (ArtifactKind != "bytecode") result["artifactKind"] = ArtifactKind;
        return result;
    }
}
