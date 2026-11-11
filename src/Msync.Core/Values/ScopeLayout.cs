using System.Collections.ObjectModel;

namespace Msync.Core;

public sealed class ScopeLayout
{
    private readonly string[] names;
    private readonly Dictionary<string, int>? offsets;

    public ScopeLayout(IEnumerable<string> names) : this(names.ToArray()) { }

    private ScopeLayout(string[] names)
    {
        this.names = names;
        if (names.Length > 4)
        {
            offsets = new(names.Length, StringComparer.Ordinal);
            for (int index = 0; index < names.Length; index++) offsets.Add(names[index], index);
        }
        else
        {
            for (int index = 0; index < names.Length; index++)
            {
                ArgumentNullException.ThrowIfNull(names[index], "key");
                for (int prior = 0; prior < index; prior++)
                    if (names[prior] == names[index])
                        throw new ArgumentException($"An item with the same key has already been added. Key: {names[index]}");
            }
        }
        Names = new ReadOnlyCollection<string>(names);
    }

    public IReadOnlyList<string> Names { get; }
    public int Count => names.Length;
    internal bool IsField(int offset, string name) =>
        (uint)offset < (uint)names.Length && names[offset] == name;

    public bool TryGetOffset(string name, out int offset)
    {
        if (offsets is not null) return offsets.TryGetValue(name, out offset);
        for (int index = 0; index < names.Length; index++)
            if (names[index] == name) { offset = index; return true; }
        ArgumentNullException.ThrowIfNull(name, "key");
        offset = 0;
        return false;
    }

    public bool TryGetOffset(ReadOnlySpan<char> name, out int offset)
    {
        if (offsets is not null)
            return offsets.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out offset);
        for (int index = 0; index < names.Length; index++)
            if (name.SequenceEqual(names[index])) { offset = index; return true; }
        offset = 0;
        return false;
    }

    public int GetOffset(string name) => TryGetOffset(name, out int offset) ? offset
        : throw new KeyNotFoundException($"The given key '{name}' was not present in the dictionary.");

    public static ScopeLayout Parse(string? members) => new(members?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? []);

    internal static ScopeLayout Own(string[] names) => new(names);
}
