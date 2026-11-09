using Msync.Core;

namespace Msync.Compiler;

internal sealed class IndexedStorage(Info element)
{
    public Info Element { get; set; } = element;
    public StorageConstraint Storage { get; init; } = new(element.Type, element.Type.WithoutNull == TypeRef.Function ? element : null, element.Storage);
}
