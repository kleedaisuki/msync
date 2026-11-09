using Msync.Core;

namespace Msync.Compiler;

internal sealed record Binding(int Slot, Info Info, Template? Self = null, EditorLocation? Origin = null,
    bool AdmitsNull = false, TypeRef? Constraint = null)
{
    public object Identity { get; init; } = new();
    public bool IsParameter { get; init; }
    public StorageConstraint Storage { get; init; } = new(Constraint, Info.Type.WithoutNull == TypeRef.Function ? Info with { Storage = null } : null, Info.Storage);
}
