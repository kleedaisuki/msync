using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Msync.Compiler.Semantics;

// Query environments borrow their parent and own only changed facts. Values are
// immutable semantic descriptions, so a child cannot mutate a parent's object.
internal sealed class SemanticFacts<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? parent = null)
    : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
{
    private Dictionary<TKey, TValue>? local;
    private Action<TKey, TValue>? changed;
    public void ObserveChanges(Action<TKey, TValue>? observer) => changed = observer;
    public IEnumerable<KeyValuePair<TKey, TValue>> LocalEntries => local is null ? [] : local;
    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set
        {
            (local ??= [])[key] = value;
            changed?.Invoke(key, value);
        }
    }
    public IEnumerable<TKey> Keys => this.Select(pair => pair.Key);
    public IEnumerable<TValue> Values => this.Select(pair => pair.Value);
    public int Count => parent is null ? local?.Count ?? 0 : this.Count(_ => true);
    public void Add(TKey key, TValue value)
    {
        (local ??= []).Add(key, value);
        changed?.Invoke(key, value);
    }
    public bool TryAdd(TKey key, TValue value)
    {
        if (ContainsKey(key)) return false;
        Add(key, value);
        return true;
    }
    public bool ContainsKey(TKey key) => local?.ContainsKey(key) == true || parent?.ContainsKey(key) == true;
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (local is not null && local.TryGetValue(key, out value)) return true;
        if (parent is not null) return parent.TryGetValue(key, out value);
        value = default;
        return false;
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        if (local is not null)
            foreach (var pair in local) yield return pair;
        if (parent is not null)
            foreach (var pair in parent)
                if (local?.ContainsKey(pair.Key) != true) yield return pair;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
