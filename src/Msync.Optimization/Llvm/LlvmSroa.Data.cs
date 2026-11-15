// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp Slice,
// AllocaSlices, Partition and partition_iterator (556–1020).
using System.Collections;
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed class Slice(ulong beginOffset, ulong endOffset, LlvmUse? use, bool splittable = false) : IComparable<Slice>
    {
        internal ulong BeginOffset { get; set; } = beginOffset;
        internal ulong EndOffset { get; set; } = endOffset;
        internal LlvmUse? Use { get; set; } = use;
        internal bool IsSplittable { get; private set; } = splittable;
        internal bool IsDead => Use is null;
        internal void MakeUnsplittable() => IsSplittable = false;
        internal void Kill() => Use = null;
        public int CompareTo(Slice? other)
        {
            if (other is null) return 1;
            int begin = BeginOffset.CompareTo(other.BeginOffset);
            if (begin != 0) return begin;
            if (IsSplittable != other.IsSplittable) return IsSplittable ? 1 : -1;
            return other.EndOffset.CompareTo(EndOffset);
        }
        internal bool SameRange(Slice other) => BeginOffset == other.BeginOffset && EndOffset == other.EndOffset && IsSplittable == other.IsSplittable;
    }

    internal sealed partial class AllocaSlices
    {
        internal LlvmAllocaInstruction Alloca { get; }
        internal LlvmInstruction? PointerEscapingInstruction { get; set; }
        internal LlvmInstruction? PointerEscapingInstructionReadOnly { get; set; }
        internal bool IsEscaped => PointerEscapingInstruction is not null;
        internal bool IsEscapedReadOnly => PointerEscapingInstructionReadOnly is not null;
        internal readonly List<Slice> Slices = [];
        internal readonly List<LlvmInstruction> DeadUsers = [];
        internal readonly List<LlvmUse> DeadUseIfPromotable = [];
        internal readonly List<LlvmUse> DeadOperands = [];
        internal void SortSlices()
        {
            var ordered = Slices.OrderBy(slice => slice).ToArray();
            Slices.Clear();
            Slices.AddRange(ordered);
        }
        internal void InsertSlices(IReadOnlyList<Slice> slices)
        {
            var added = slices.OrderBy(slice => slice).ToArray();
            List<Slice> merged = new(Slices.Count + added.Length);
            int i = 0, j = 0;
            while (i < Slices.Count && j < added.Length)
                merged.Add(Slices[i].CompareTo(added[j]) <= 0 ? Slices[i++] : added[j++]);
            while (i < Slices.Count) merged.Add(Slices[i++]);
            while (j < added.Length) merged.Add(added[j++]);
            Slices.Clear();
            Slices.AddRange(merged);
        }
        internal IEnumerable<Partition> Partitions()
        {
            var iterator = new PartitionIterator(this);
            while (iterator.MoveNext()) yield return iterator.Current;
        }
    }

    // This view is the source iterator's current Partition, not an owned
    // snapshot. Rewriters consume it before advancing the enclosing iterator.
    internal sealed class Partition(AllocaSlices owner) : IEnumerable<Slice>
    {
        internal ulong BeginOffset, EndOffset;
        internal int StartIndex, EndIndex;
        internal readonly List<Slice> SplitTails = [];
        internal IReadOnlyList<Slice> SplitSliceTails => SplitTails;
        internal bool IsEmpty => StartIndex == EndIndex;
        internal ulong Size { get { Debug.Assert(BeginOffset < EndOffset); return EndOffset - BeginOffset; } }
        public IEnumerator<Slice> GetEnumerator()
        {
            for (int i = StartIndex; i < EndIndex; ++i) yield return owner.Slices[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class PartitionIterator(AllocaSlices owner)
    {
        internal Partition Current { get; } = new(owner);
        private ulong maxSplitEnd;
        private bool started;
        internal bool MoveNext()
        {
            if (!started)
            {
                started = true;
                if (owner.Slices.Count == 0) return false;
            }
            Advance();
            return Current.StartIndex != owner.Slices.Count || Current.SplitTails.Count != 0;
        }
        private void Advance()
        {
            var p = Current;
            Debug.Assert(p.StartIndex != owner.Slices.Count || p.SplitTails.Count != 0);
            if (p.SplitTails.Count != 0)
            {
                if (p.EndOffset >= maxSplitEnd) { p.SplitTails.Clear(); maxSplitEnd = 0; }
                else p.SplitTails.RemoveAll(slice => slice.EndOffset <= p.EndOffset);
            }
            if (p.StartIndex == owner.Slices.Count)
            {
                Debug.Assert(p.SplitTails.Count == 0);
                return;
            }
            if (p.StartIndex != p.EndIndex && AdvancePriorPartition(p)) return;
            var first = owner.Slices[p.StartIndex];
            p.BeginOffset = p.SplitTails.Count == 0 ? first.BeginOffset : p.EndOffset;
            p.EndOffset = first.EndOffset;
            ++p.EndIndex;
            if (!first.IsSplittable)
            {
                while (p.EndIndex != owner.Slices.Count && owner.Slices[p.EndIndex].BeginOffset < p.EndOffset)
                {
                    var next = owner.Slices[p.EndIndex++];
                    if (!next.IsSplittable) p.EndOffset = Math.Max(p.EndOffset, next.EndOffset);
                }
                return;
            }
            while (p.EndIndex != owner.Slices.Count && owner.Slices[p.EndIndex].BeginOffset < p.EndOffset && owner.Slices[p.EndIndex].IsSplittable)
                p.EndOffset = Math.Max(p.EndOffset, owner.Slices[p.EndIndex++].EndOffset);
            if (p.EndIndex != owner.Slices.Count && owner.Slices[p.EndIndex].BeginOffset < p.EndOffset)
                p.EndOffset = owner.Slices[p.EndIndex].BeginOffset;
        }
        private bool AdvancePriorPartition(Partition p)
        {
            foreach (var slice in p)
            {
                if (!slice.IsSplittable || slice.EndOffset <= p.EndOffset) continue;
                p.SplitTails.Add(slice);
                maxSplitEnd = Math.Max(maxSplitEnd, slice.EndOffset);
            }
            p.StartIndex = p.EndIndex;
            if (p.StartIndex == owner.Slices.Count)
            {
                p.BeginOffset = p.EndOffset;
                p.EndOffset = maxSplitEnd;
                return true;
            }
            var first = owner.Slices[p.StartIndex];
            if (p.SplitTails.Count == 0 || first.BeginOffset == p.EndOffset || first.IsSplittable) return false;
            p.BeginOffset = p.EndOffset;
            p.EndOffset = first.BeginOffset;
            return true;
        }
    }

    internal sealed class OrderedSet<T> : IReadOnlyCollection<T> where T : notnull
    {
        private List<T> values = [];
        private readonly HashSet<T> set = [];
        public int Count => values.Count;
        internal IReadOnlyList<T> Values => values;
        internal bool Add(T value) { if (!set.Add(value)) return false; values.Add(value); return true; }
        internal bool Insert(T value) => Add(value);
        internal bool Contains(T value) => set.Contains(value);
        internal void AddRange(IEnumerable<T> added) { foreach (var value in added) Add(value); }
        internal bool Remove(T value) { if (!set.Remove(value)) return false; values.Remove(value); return true; }
        internal T PopBack() { var value = values[^1]; values.RemoveAt(values.Count - 1); set.Remove(value); return value; }
        internal void Clear() { values.Clear(); set.Clear(); }
        internal void ExceptWith(IReadOnlySet<T> removed) { values.RemoveAll(value => removed.Contains(value)); set.ExceptWith(removed); }
        internal List<T> TakeVector() { var result = values; values = []; set.Clear(); return result; }
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal sealed class OrderedMap<TKey, TValue> where TKey : notnull
    {
        private readonly List<TKey> keys = [];
        private readonly Dictionary<TKey, TValue> values = [];
        internal int Count => keys.Count;
        internal bool Add(TKey key, TValue value)
        {
            if (!values.TryAdd(key, value)) return false;
            keys.Add(key);
            return true;
        }
        internal bool Insert(KeyValuePair<TKey, TValue> value) => Add(value.Key, value.Value);
        internal TValue this[TKey key]
        {
            get => values[key];
            set { if (!values.ContainsKey(key)) keys.Add(key); values[key] = value; }
        }
        internal List<KeyValuePair<TKey, TValue>> TakeVector()
        {
            var result = keys.Select(key => KeyValuePair.Create(key, values[key])).ToList();
            keys.Clear(); values.Clear(); return result;
        }
    }
}
