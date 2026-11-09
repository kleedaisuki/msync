using System.Collections;

namespace Msync.Optimization;

/// <summary>Immutable reaching definitions; ordinary values need no set allocation.</summary>
internal readonly struct DefinitionSet : IReadOnlyCollection<int>
{
    private readonly int single;
    private readonly int[]? multiple;
    public int Count { get; }

    internal DefinitionSet(int token) { single = token; Count = 1; }
    private DefinitionSet(int[] tokens) { multiple = tokens; Count = tokens.Length; }

    internal bool Contains(int token) => Count == 1 ? single == token
        : multiple is not null && Array.BinarySearch(multiple, token) >= 0;

    internal bool Overlaps(HashSet<int> other)
    {
        foreach (var token in this) if (other.Contains(token)) return true;
        return false;
    }

    internal DefinitionSet Union(DefinitionSet other)
    {
        if (other.Count == 0) return this;
        if (Count == 0) return other;
        if (Count == 1 && other.Count == 1)
            return single == other.single ? this : new(single < other.single ? [single, other.single] : [other.single, single]);
        if (ReferenceEquals(multiple, other.multiple) && multiple is not null) return this;
        if (other.Count == 1) return Add(other.single);
        if (Count == 1) return other.Add(single);
        return Merge(other);
    }

    private DefinitionSet Add(int token)
    {
        var position = Array.BinarySearch(multiple!, token);
        if (position >= 0) return this;
        position = ~position;
        var result = new int[Count + 1];
        multiple.AsSpan(0, position).CopyTo(result);
        result[position] = token;
        multiple.AsSpan(position).CopyTo(result.AsSpan(position + 1));
        return new(result);
    }

    private DefinitionSet Merge(DefinitionSet other)
    {
        var left = multiple!; var right = other.multiple!;
        var count = Count;
        var a = 0;
        foreach (var token in right)
        {
            while (a < left.Length && left[a] < token) a++;
            if (a == left.Length || left[a] != token) count++;
        }
        if (count == Count) return this;
        if (count == other.Count) return other;
        var result = new int[count];
        a = 0; var b = 0; var output = 0;
        while (a < left.Length && b < right.Length)
        {
            if (left[a] == right[b]) { result[output++] = left[a++]; b++; }
            else result[output++] = left[a] < right[b] ? left[a++] : right[b++];
        }
        left.AsSpan(a).CopyTo(result.AsSpan(output));
        right.AsSpan(b).CopyTo(result.AsSpan(output + left.Length - a));
        return new(result);
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<int>
    {
        private readonly DefinitionSet set;
        private int index;
        internal Enumerator(DefinitionSet set) { this.set = set; index = -1; }
        public readonly int Current => set.multiple is null ? set.single : set.multiple[index];
        readonly object IEnumerator.Current => Current;
        public bool MoveNext() => ++index < set.Count;
        public void Reset() => index = -1;
        public readonly void Dispose() { }
    }
}
