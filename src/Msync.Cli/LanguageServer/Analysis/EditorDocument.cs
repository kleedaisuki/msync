using System.Text.Json;
using Msync.Compiler;

namespace Msync.Cli;

internal sealed record EditorDocument(SourceText Source, int Version, long Generation)
{
    public EditorAnalysis? Analysis { get; set; }
    public JsonElement? SemanticTokenData { get; set; }
    public EditorDocument(string text, int version, long generation) : this(new SourceText(text), version, generation) { }
}
