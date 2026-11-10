using System.Collections;

namespace Msync.Runtime;

internal sealed class PreparationTable<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
{
    private readonly List<T> additions = [];
    public int Count => source.Count + additions.Count;
    public T this[int index]
    {
        get => index < source.Count ? source[index] : additions[index - source.Count];
        set => additions[index - source.Count] = value;
    }
    internal void Add(T value) => additions.Add(value);
    public IEnumerator<T> GetEnumerator()
    {
        for (int index = 0; index < source.Count; index++) yield return source[index];
        foreach (T value in additions) yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class PreparationFunctions(IReadOnlyList<Msync.Core.FunctionCode> source,
    int changed, Msync.Core.FunctionCode replacement) : IReadOnlyList<Msync.Core.FunctionCode>
{
    public int Count => source.Count;
    public Msync.Core.FunctionCode this[int index] => index == changed ? replacement : source[index];
    public IEnumerator<Msync.Core.FunctionCode> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
