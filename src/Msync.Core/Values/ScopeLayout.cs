using System.Collections.ObjectModel;

namespace Msync.Core;

public sealed class ScopeLayout
{
    private readonly Dictionary<string, int> offsets;

    public ScopeLayout(IEnumerable<string> names) : this(names.ToArray()) { }

    private ScopeLayout(string[] names)
    {
        offsets = new(names.Length, StringComparer.Ordinal);
        for (int index = 0; index < names.Length; index++) offsets.Add(names[index], index);
        Names = new ReadOnlyCollection<string>(names);
    }

    public IReadOnlyList<string> Names { get; }
    public int Count => Names.Count;
    public bool TryGetOffset(string name, out int offset) => offsets.TryGetValue(name, out offset);
    public bool TryGetOffset(ReadOnlySpan<char> name, out int offset) =>
        offsets.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out offset);
    public int GetOffset(string name) => offsets[name];

    public static ScopeLayout Parse(string? members) => new(members?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? []);

    internal static ScopeLayout Own(string[] names) => new(names);
}
