namespace Msync.Optimization;

internal enum MemoryRootKind : byte { Capture, Parameter, Allocation }
internal readonly record struct MemoryRoot(MemoryRootKind Kind, int Index);

internal sealed class MemoryAccess : IEquatable<MemoryAccess>
{
    internal MemoryRoot Root { get; }
    internal IReadOnlyList<string> Fields { get; }
    internal bool Descendants { get; }
    internal MemoryAccess(MemoryRoot root, IReadOnlyList<string>? fields = null, bool descendants = false)
    { Root = root; Fields = fields ?? []; Descendants = descendants; }
    internal MemoryAccess Field(string name) => Descendants ? this : new(Root, [.. Fields, name]);
    internal MemoryAccess Subtree() => Descendants ? this : new(Root, Fields, true);
    internal bool PrefixOf(MemoryAccess other)
    {
        if (Root != other.Root || Fields.Count > other.Fields.Count) return false;
        for (int index = 0; index < Fields.Count; index++) if (Fields[index] != other.Fields[index]) return false;
        return true;
    }
    public bool Equals(MemoryAccess? other) => other is not null && Root == other.Root && Descendants == other.Descendants
        && Fields.SequenceEqual(other.Fields, StringComparer.Ordinal);
    public override bool Equals(object? other) => other is MemoryAccess access && Equals(access);
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Root); hash.Add(Descendants);
        foreach (string field in Fields) hash.Add(field, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
    public override string ToString() => $"{Root.Kind}[{Root.Index}]" + string.Concat(Fields.Select(field => "::" + field)) + (Descendants ? "::*" : "");
}
