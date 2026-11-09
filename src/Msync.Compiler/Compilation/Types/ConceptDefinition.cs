using System.Collections.Immutable;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed record ConceptDefinition(ConceptDeclaration Syntax, string Source, string SourceName,
    ImmutableDictionary<string, ConceptDefinition> Environment)
{
    public bool StandardShadowed { get; init; }
}
internal sealed record ConceptContract(TypeSyntax Annotation, TypeEnvironment Environment);
internal sealed record ConceptMemberContract(ConceptDefinition Definition, ConceptMember Requirement, IReadOnlyDictionary<string, Info> Associated);
internal sealed record TypeEnvironment(ImmutableDictionary<string, ConceptDefinition> Concepts, bool StandardShadowed,
    string Source, string SourceName);
