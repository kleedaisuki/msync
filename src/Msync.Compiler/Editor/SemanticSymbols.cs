using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record SemanticMember(string Name, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null)
{
    public bool IsConstraint { get; init; }
}
public sealed record SemanticOccurrence(SourceSpan Span, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null);
public sealed record EditorLocation(string SourceName, string Source, SourceSpan Span);

public enum EditorSymbolKind { Variable, Function, Scope, Field, Parameter }
public sealed record EditorSymbol(string Name, SourceSpan Span, SourceSpan SelectionSpan, EditorSymbolKind Kind,
    IReadOnlyList<EditorSymbol> Children);
