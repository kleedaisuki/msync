// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/ScopedHashTable.h.
// Managed nodes retain the source per-key binding chain and scope undo chain.
namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private sealed class ScopedTable<TKey, TValue>(IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        private sealed record Binding(TKey Key, TValue Value, Binding? Previous);
        private readonly Dictionary<TKey, Binding> heads = new(comparer);
        private readonly List<Binding> inserted = [];
        internal int Mark => inserted.Count;
        internal bool Contains(TKey key) => heads.ContainsKey(key);
        internal TValue? Lookup(TKey key) => heads.TryGetValue(key, out var binding) ? binding.Value : default;
        internal void Insert(TKey key, TValue value)
        {
            var binding = new Binding(key, value, heads.GetValueOrDefault(key));
            heads[key] = binding;
            inserted.Add(binding);
        }
        internal void Pop(int mark)
        {
            while (inserted.Count != mark)
            {
                var binding = inserted[^1];
                if (binding.Previous is null) heads.Remove(binding.Key);
                else heads[binding.Key] = binding.Previous;
                inserted.RemoveAt(inserted.Count - 1);
            }
        }
    }
}
