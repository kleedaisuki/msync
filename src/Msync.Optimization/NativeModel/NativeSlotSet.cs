using System.Collections;
using System.Numerics;

namespace Msync.Optimization;

internal sealed class NativeSlotSet : IEnumerable<int>
{
    private readonly ulong[] words;
    internal NativeSlotSet(int slots) => words = new ulong[(slots + 63) / 64];
    private NativeSlotSet(ulong[] words) => this.words = words;
    internal int WordCount => words.Length;
    internal NativeSlotSet Copy() => new((ulong[])words.Clone());
    internal bool Contains(int slot) => (words[slot >> 6] & (1UL << (slot & 63))) != 0;
    internal void Add(int slot) => words[slot >> 6] |= 1UL << (slot & 63);
    internal void Remove(int slot) => words[slot >> 6] &= ~(1UL << (slot & 63));
    internal void UnionWith(NativeSlotSet source)
    {
        for (int index = 0; index < words.Length; index++) words[index] |= source.words[index];
    }
    internal void ExceptWith(NativeSlotSet source)
    {
        for (int index = 0; index < words.Length; index++) words[index] &= ~source.words[index];
    }
    internal bool Same(NativeSlotSet other) => words.AsSpan().SequenceEqual(other.words);
    public IEnumerator<int> GetEnumerator()
    {
        for (int index = 0; index < words.Length; index++)
        {
            ulong pending = words[index];
            while (pending != 0)
            {
                yield return index * 64 + BitOperations.TrailingZeroCount(pending);
                pending &= pending - 1;
            }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
