namespace Msync.Optimization;

[Flags]
internal enum IndexedRegion : byte
{
    None = 0,
    Contents = 1,
    Length = 2
}

internal enum IndexedReadKind : byte { None, Element, Count }

internal readonly record struct IndexedFootprint(MemoryAccess? Receiver, IndexedRegion Region, int? Index = null)
{
    public override string ToString() => (Receiver?.ToString() ?? "Vector[*]") + (Region == IndexedRegion.Length
        ? "::length" : Index is int index ? $"::contents[{index}]" : "::contents[*]");
}

internal readonly record struct IndexedNativeEffect(IndexedRegion Reads, IndexedRegion Writes, int Indices = 0)
{
    private const IndexedRegion Contents = IndexedRegion.Contents, Length = IndexedRegion.Length;

    internal static IndexedNativeEffect For(string name) => name switch
    {
        "storage.vector_size" => new(Length, 0),
        "storage.vector_get" => new(Contents | Length, 0, 1),
        "storage.vector_set" => new(Length, Contents, 1),
        "storage.vector_snapshot" => new(Contents | Length, 0),
        "storage.vector_push" or "storage.vector_insert" =>
            new(Length, Contents | Length),
        "storage.vector_pop" or "storage.vector_remove" or "storage.vector_take" =>
            new(Contents | Length, Contents | Length),
        "storage.vector_clear" => new(0, Contents | Length),
        _ => default
    };

    internal static IndexedReadKind Read(string? name) => name switch
    {
        "storage.vector_size" => IndexedReadKind.Count,
        "storage.vector_get" => IndexedReadKind.Element,
        _ => IndexedReadKind.None
    };

    internal static bool Allocates(string name) => name is "storage.vector_new"
        or "storage.vector_empty" or "storage.vector_empty_like";

    internal static int? ExactIndex(double value) =>
        value >= 0 && value <= int.MaxValue && value == Math.Truncate(value) ? (int)value : null;
}
