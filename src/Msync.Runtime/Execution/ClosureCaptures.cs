using System.Collections;
using Msync.Core;

namespace Msync.Runtime;

/// <summary>VM-owned immutable capture references; a referenced Cell remains mutable.</summary>
internal sealed class ClosureCaptures(Value[] values) : IReadOnlyList<Value>
{
    public int Count => values.Length;
    public Value this[int index] => values[index];
    internal void CopyTo(Span<Value> destination) => values.AsSpan().CopyTo(destination);
    public IEnumerator<Value> GetEnumerator() => ((IEnumerable<Value>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static IReadOnlyList<Value> Own(Value[] values) =>
        values.Length <= 1 ? values : new ClosureCaptures(values);
}
