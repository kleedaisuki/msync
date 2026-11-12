namespace Msync.Core;

/// <summary>A live, noniterable owner of collection storage.</summary>
public abstract record StorageValue : Value
{
    public abstract TypeRef ElementType { get; }
    public abstract IEnumerable<Value> RetainedReferences();
    public sealed override TypeRef Type => new(TypeKind.Storage, ElementType);
}
