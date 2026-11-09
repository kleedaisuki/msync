using System.Collections.Immutable;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed class StorageConstraint(TypeRef? type, Info? callables = null, StorageConstraint? source = null)
{
    public TypeRef? Type { get; set; } = type;
    public bool IsUnwritten { get; set; }
    public Info? Callables { get; set; } = callables;
    public ImmutableDictionary<string, ConceptContract> ConceptContracts { get; set; } = ImmutableDictionary<string, ConceptContract>.Empty;
    public ImmutableDictionary<string, ConceptMemberContract> ConceptMemberContracts { get; set; } = ImmutableDictionary<string, ConceptMemberContract>.Empty;
    public int Revision { get; set; }
    public ImmutableHashSet<StorageConstraint> MemoryAliases { get; set; } = [];
    public ImmutableHashSet<StorageConstraint> Sources { get; set; } = source is null ? [] : [source];
    public ImmutableDictionary<string, CallableContract> Contracts { get; set; } = ImmutableDictionary<string, CallableContract>.Empty;
    public ImmutableDictionary<string, CastTypeContract> CastContracts { get; set; } = ImmutableDictionary<string, CastTypeContract>.Empty;
    public ImmutableDictionary<string, DeferredTypeWrite> DeferredTypeWrites { get; set; } = ImmutableDictionary<string, DeferredTypeWrite>.Empty;
}
internal sealed record CallableContract(Info Result, string Source, string SourceName, SourceSpan Span);
internal sealed record CastTypeContract(TypeRef Input, TypeRef Target, string Source, string SourceName, SourceSpan Span);
internal sealed record DeferredTypeWrite(TypeRef? Target, string Source, string SourceName, SourceSpan Span);
