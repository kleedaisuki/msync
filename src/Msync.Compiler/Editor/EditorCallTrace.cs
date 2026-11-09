using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed record EditorCallTrace(string SourceName, string Source, SourceSpan Span,
    IReadOnlyList<Parameter> Parameters, IReadOnlyList<Msync.Core.TypeRef> Types, EditorCallTrace? Parent)
{
    public DiagnosticLocation Location() => new(SourceName, Source, Span, Parameters.Count == 0 ? "while checking this call"
        : $"while checking this call with {string.Join(", ", Parameters.Zip(Types, (parameter, type) => $"{parameter.Name}: {type}"))}");
}
