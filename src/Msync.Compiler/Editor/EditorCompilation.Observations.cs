using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    internal static IReadOnlyDictionary<string, (NativeDeclaration Declaration, string Source)> StandardNativeDeclarations() =>
        libraries.Value.SelectMany(library => library.Value.Document.Statements.OfType<NativeDeclaration>()
            .Select(declaration => new KeyValuePair<string, (NativeDeclaration, string)>(library.Key + "." + declaration.Name,
                (declaration, library.Value.Source)))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private readonly Dictionary<Info, string>? editorConceptRequirements = observe is null ? null : new(ReferenceEqualityComparer.Instance);

    private void DescribeConceptRequirement(Info info, ConceptDefinition definition, ConceptMember requirement)
    {
        if (editorConceptRequirements is not null)
            editorConceptRequirements[info] = definition.Source[requirement.Span.Start..requirement.Span.End];
    }

    internal string? ConceptRequirementSignature(Info info) => editorConceptRequirements?.GetValueOrDefault(info);

    private void Observe(Frame frame, SourceSpan span, Info info, string? name = null)
    {
        if (observe is null || !IsCurrentSource(frame)) return;
        observe(span, info, name, frame.EditorReportErrors);
    }
}
