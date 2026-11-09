using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

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
        Dictionary<SourceSpan, SemanticOccurrence> occurrences = syntax.Occurrences.ToDictionary(item => item.Span);
        foreach (var occurrence in contexts.Occurrences()) occurrences[occurrence.Span] = occurrence;
        foreach (var (span, occurrence) in occurrences.ToArray())
        {
            if (occurrence.Type != "Type" || occurrence.SemanticRole != "type"
                || LanguageTypes.TryResolve(occurrence.Signature, out _)) continue;
            var concept = contexts.ConceptsAt(span.Start).Concat(StandardConceptBindings)
                .FirstOrDefault(item => item.Name == occurrence.Signature || item.Signature == occurrence.Signature);
            if (concept is not null) occurrences[span] = occurrence with
            { Signature = concept.Signature, Description = concept.Description, Members = concept.Members };
        }
        return new(occurrences.Values.OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToArray(),
            parsed.Error?.Diagnostic ?? semantic.Diagnostic)
        {
            SemanticContexts = contexts, RegexSpans = parsed.RegexSpans, Symbols = syntax.Symbols,
            TypeSpans = syntax.TypeSpans, AssociatedTypes = syntax.AssociatedTypes,
            AssociatedDefinitions = syntax.AssociatedDefinitions
        };
    }

}
