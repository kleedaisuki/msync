namespace Msync.Compiler.Semantics;

internal enum IndexedNativeOperation : byte
{
    None, Vector, Typed, EmptyLike, Element, Snapshot, StoreElement, StoreAt, Reset, Effect
}

internal static class IndexedNative
{
    internal static IndexedNativeOperation Classify(string name) => name switch
    {
        "storage.vector_new" => IndexedNativeOperation.Vector,
        "storage.vector_empty" or "arena.new" or "slots.new" => IndexedNativeOperation.Typed,
        "storage.vector_empty_like" => IndexedNativeOperation.EmptyLike,
        "storage.vector_get" or "storage.vector_pop" or "storage.vector_remove" or "arena.key" or "slots.get" =>
                IndexedNativeOperation.Element,
        "storage.vector_snapshot" or "storage.vector_take" => IndexedNativeOperation.Snapshot,
        "storage.vector_push" or "arena.allocate" or "slots.allocate" =>
            IndexedNativeOperation.StoreElement,
        "slots.reset" => IndexedNativeOperation.Reset,
        "storage.vector_set" or "storage.vector_insert" => IndexedNativeOperation.StoreAt,
        "storage.vector_clear" or "storage.vector_size" or "arena.release" or "arena.clear"
            or "slots.release" or "slots.count" =>
                IndexedNativeOperation.Effect,
        _ => IndexedNativeOperation.None
    };

    internal static int WriteIndex(IndexedNativeOperation operation) => operation switch
    {
        IndexedNativeOperation.StoreElement or IndexedNativeOperation.Reset => 1,
        IndexedNativeOperation.StoreAt => 2,
        _ => -1
    };
}
