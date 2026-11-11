namespace Msync.Optimization;

[Flags]
internal enum IndexedRegion : byte
{
    None = 0,
    Contents = 1,
    Length = 2
}

internal readonly record struct IndexedFootprint(MemoryAccess Receiver, IndexedRegion Region, int? Index = null)
{
    public override string ToString() => Receiver + (Region == IndexedRegion.Length
        ? "::length" : Index is int index ? $"::contents[{index}]" : "::contents[*]");
}

internal readonly record struct IndexedNativeEffect(IndexedRegion Reads, IndexedRegion Writes, int Indices = 0)
{
    private const IndexedRegion Contents = IndexedRegion.Contents, Length = IndexedRegion.Length;

    internal static IndexedNativeEffect For(string name) => name switch
    {
        "storage.buffer_size" => new(Length, 0),
        "storage.buffer_get" => new(Contents | Length, 0, 1),
        "storage.buffer_set" => new(Length, Contents, 1),
        "storage.buffer_swap" => new(Contents | Length, Contents, 2),
        "storage.buffer_snapshot" => new(Contents | Length, 0),
        "storage.buffer_push" or "storage.buffer_insert" => new(Length, Contents | Length),
        "storage.buffer_pop" or "storage.buffer_remove" => new(Contents | Length, Contents | Length),
        "storage.buffer_clear" => new(0, Contents | Length),
        _ => default
    };

    internal static int? ExactIndex(double value) =>
        value >= 0 && value <= int.MaxValue && value == Math.Truncate(value) ? (int)value : null;
}
