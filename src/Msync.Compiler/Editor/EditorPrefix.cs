using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed class EditorPrefixChanged : Exception;

// One stable lexical cut, not an unbounded history of edited functions. The
// retained arena is owned by the document's analysis worker. Every trial suffix
// is transactional, and only its immutable editor projection can escape.
internal sealed class EditorPrefix(string text, int statementCount, BoundProgram bound,
    SemanticContext facts, SemanticInstanceCache instances, EditorAnalysis projection)
{
    internal static bool Independent(Document document)
    {
        return document.Statements.All(IndependentNode);

        static bool IndependentNode(Node node) => node switch
        {
            AssignmentExpr or GlobalStatement or NativeDeclaration or ScopeDeclaration
                or ScopeExpr { Name: not null } or UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } } => false,
            _ => SyntaxTraversal.Children(node).All(IndependentNode)
        };
    }

    internal static EditorPrefix? Create(string previous, EditorParseResult old, string source,
        EditorParseResult parsed, string sourceName, CancellationToken cancellationToken)
    {
        if (old.Error is not null || !Independent(parsed.Document)) return null;
        int matching = 0;
        while (matching < old.Document.Statements.Count && matching < parsed.Document.Statements.Count)
        {
            SourceSpan span = old.Document.Statements[matching].Span;
            if (span != parsed.Document.Statements[matching].Span
                || !previous.AsSpan(span.Start, span.Length).SequenceEqual(source.AsSpan(span.Start, span.Length))) break;
            matching++;
        }
        if (matching == 0 || matching == parsed.Document.Statements.Count) return null;
        int end = parsed.Document.Statements[matching - 1].Span.End;
        if (parsed.Error is { } error && error.Span.Start < end) return null;
        if (source[end - 1] is not (';' or '}')) return null;
        string prefixText = source[..end];
        // Inter-statement comments and trivia can affect declaration help.
        if (!previous.AsSpan().StartsWith(prefixText, StringComparison.Ordinal)) return null;
        var prefix = Slice(parsed, 0, matching, 0, end, keepError: false);
        var bound = new SourceBinder(observations: true, editorMode: true, cancellationToken: cancellationToken)
            .Bind(prefixText, sourceName, prefix.Document);
        if (bound.Diagnostics.Count != 0 || bound.WrittenLocations.Count != 0
            || bound.StandardLocations.Count != 0 || bound.NextSingleton != 0) return null;
        var observations = new SemanticObservationBuilder(bound, cancellationToken);
        SemanticInstanceCache instances = new();
        var analyzer = new SemanticAnalyzer(bound, observations, instances);
        try { analyzer.AnalyzeForEditor(); }
        catch (CompileException) { return null; }
        SemanticContext final = analyzer.FinalState;
        if (analyzer.InspectedInvocationGraph || final.Returns.Count != 0
            || final.CellWrites.Count != 0 || final.FieldWrites.Count != 0 || final.IndexedWrites.Count != 0
            || bound.Hir.Types[bound.Hir.Functions[bound.Entry.Value].Result.Value].RuntimeType.Kind == TypeKind.Never)
            return null;
        EditorAnalysis projection = EditorCompiler.ProjectSemantic(prefixText, sourceName, prefix,
            new(observations.Freeze(), null));
        SemanticContext facts = final.QueryFork();
        facts.DetachFacts();
        return new(prefixText, matching, bound, facts, instances, projection);
    }

    internal bool Matches(string source, EditorParseResult parsed) =>
        parsed.Document.Statements.Count > statementCount
        && parsed.Document.Statements[statementCount - 1].Span.End == text.Length
        && source.AsSpan().StartsWith(text, StringComparison.Ordinal)
        && (parsed.Error is null || parsed.Error.Span.Start >= text.Length);

    internal EditorAnalysis? Analyze(string source, EditorParseResult parsed, string sourceName,
        CancellationToken cancellationToken)
    {
        var suffix = Slice(parsed, statementCount, parsed.Document.Statements.Count - statementCount,
            text.Length, source.Length, keepError: true);
        if (!Independent(suffix.Document)) return null;
        using var transaction = bound.Hir.BeginTransaction();
        using var additions = instances.BeginAdditions();
        var appended = new SourceBinder(bound, observations: true, editorMode: true, appendDocument: true,
            cancellationToken: cancellationToken).Bind(source, sourceName, suffix.Document);
        if (appended.WrittenLocations.Count != 0 || appended.StandardLocations.Count != 0
            || appended.NextSingleton != 0) return null;
        var observations = new SemanticObservationBuilder(appended, cancellationToken);
        var analyzer = new SemanticAnalyzer(appended, observations, instances,
            reusedEditorTemplates: bound.Templates.Count);
        SourceDiagnostic? diagnostic = appended.Diagnostics.FirstOrDefault();
        try { analyzer.AnalyzeForEditor(facts); }
        catch (EditorPrefixChanged) { return null; }
        catch (CompileException error) { diagnostic ??= error.Diagnostic; }
        var analysis = EditorCompiler.ProjectSemantic(source, sourceName, suffix, new(observations.Freeze(), diagnostic));
        return EditorAnalysis.Join(projection, analysis, text.Length, sourceName, source);
    }

    private static EditorParseResult Slice(EditorParseResult parsed, int start, int count,
        int from, int to, bool keepError) => new(
        new(parsed.Document.Statements.Skip(start).Take(count).ToArray(), new(from, to - from))
        { EditorError = keepError ? parsed.Error : null },
        keepError ? parsed.Error : null, keepError ? parsed.IncompleteStart : null)
        {
            RegexSpans = parsed.RegexSpans.Where(span => span.Start >= from && span.End <= to).ToArray(),
            Tokens = parsed.Tokens.Where(token => token.Kind == TokenKind.Number
                && token.Span.Start >= from && token.Span.End <= to).ToArray()
        };
}
