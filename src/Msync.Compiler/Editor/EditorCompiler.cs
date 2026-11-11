using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

public static class EditorCompiler
{
    public static IReadOnlyList<SemanticMember> BuiltinBindings => SemanticStandardCatalog.Builtins;
    public static IReadOnlyList<SemanticMember> StandardConceptBindings => SemanticStandardCatalog.Concepts;

    public static EditorAnalysis Analyze(string source, string sourceName)
    {
        EditorParseResult parsed = Parser.ParseForEditor(source, sourceName);
        SemanticEditorResult semantic = SemanticEditorCompiler.Analyze(source, sourceName, parsed.Document);
        return ProjectSemantic(source, sourceName, parsed, semantic);
    }

    internal static EditorAnalysis ProjectSemantic(string source, string sourceName,
        EditorParseResult parsed, SemanticEditorResult semantic)
    {
        EditorSyntaxIndex syntax = EditorSyntaxIndex.Create(parsed.Document, sourceName, source);
        EditorSemanticIndex contexts = new(semantic.View, SemanticStandardCatalog.Native);
        return new(new EditorOccurrences(syntax, contexts),
            parsed.Error?.Diagnostic ?? semantic.Diagnostic)
        {
            SemanticContexts = contexts,
            RegexSpans = parsed.RegexSpans,
            NumberSpans = parsed.Tokens.Count == 0 ? null
                : parsed.Tokens.Where(token => token.Kind == TokenKind.Number).Select(token => token.Span).ToArray(),
            Symbols = syntax.Symbols,
            TypeSpans = syntax.TypeSpans,
            AssociatedTypes = syntax.AssociatedTypes,
            AssociatedDefinitions = syntax.AssociatedDefinitions
        };
    }

}
