using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed class Template(int id, FunctionExpr syntax, IReadOnlyList<KeyValuePair<string, Binding>> captures, string source, string sourceName)
{
    public int Id { get; } = id;
    public FunctionExpr Syntax { get; } = syntax;
    public IReadOnlyList<KeyValuePair<string, Binding>> Captures { get; } = captures;
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public Dictionary<string, Specialization> Specializations { get; } = [];
    public ImmutableDictionary<string, ConceptDefinition> Concepts { get; init; } = ImmutableDictionary<string, ConceptDefinition>.Empty;
    public bool StandardShadowed { get; init; }
    public bool Instance { get; init; }
}
internal sealed class Specialization(int id, Info result, IReadOnlyList<TypeRef> parameterTypes)
{
    public int Id { get; } = id;
    public Info Result { get; set; } = result;
    public IReadOnlyList<TypeRef> ParameterTypes { get; } = parameterTypes;
    public IReadOnlyDictionary<object, Info> Effects { get; set; } = new Dictionary<object, Info>();
}
