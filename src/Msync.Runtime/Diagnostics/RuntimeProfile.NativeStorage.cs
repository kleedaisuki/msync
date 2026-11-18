using Msync.Core.Native;

namespace Msync.Runtime;

public sealed partial class RuntimeProfile
{
    private NativeStorageCounters? nativeStorage;
    internal NativeStorageCounters StorageCounters => LazyInitializer.EnsureInitialized(ref nativeStorage,
        static () => new NativeStorageCounters());

    private void CollectNativeStorage(List<RuntimeProfileSample> samples) => nativeStorage?.Collect(samples);

    internal sealed class NativeStorageCounters
    {
        private const int Union = (int)NativeKind.MachineFloat + 1, Never = Union + 1, Buckets = Never + 1, Width = 6;
        private const int Writes = 0, Copies = 1, Clears = 2, Flags = 3, Avoided = 4, PendingCopies = 5;
        private long pendingOpens, pendingResizes, pendingBytes, pendingHits, pendingMisses, pendingAdds, pendingReplaces, pendingRetired;
        private readonly long[] counts = new long[Buckets * Width];

        internal void Write(NativeType type) => Record(type, Writes, Math.Max(1, type.Representation.Bytes));
        internal void Clear(NativeType type) => Record(type, Clears, Math.Max(1, type.Representation.Bytes));
        internal void Copy(NativeType type) => Record(type, Copies, Math.Max(1, type.Representation.Bytes));
        internal void PendingCopy(NativeType type) => Interlocked.Increment(ref counts[Index(type) + PendingCopies]);
        internal void CopyKind(NativeType source, NativeType destination, NativeKind kind)
        {
            int bytes = source.Machine?.ByteSize ?? NativeRepresentation.Of(kind).Bytes;
            Record(destination, Copies, Math.Max(1, bytes) + (destination.IsUnion ? 1 : 0));
        }
        internal void Scalar(NativeType type, NativeKind kind) => Record(type, Writes,
            (kind == NativeKind.Number ? 8 : 1) + (type.Representation.PayloadOffset != 0 ? 1 : 0));
        internal void Address(NativeType type) => Record(type, Writes, 8);

        internal void PendingIndexReserve(long bytes, bool opened)
        {
            if (opened) Interlocked.Increment(ref pendingOpens);
            else Interlocked.Increment(ref pendingResizes);
            Interlocked.Add(ref pendingBytes, bytes);
        }

        internal void PendingIndexLookup(bool hit)
        {
            if (hit) Interlocked.Increment(ref pendingHits);
            else Interlocked.Increment(ref pendingMisses);
        }

        internal void PendingIndexWrite(bool added)
        {
            if (added) Interlocked.Increment(ref pendingAdds);
            else Interlocked.Increment(ref pendingReplaces);
        }

        internal void PendingIndexRetire(int count) => Interlocked.Add(ref pendingRetired, count);

        private static int Index(NativeType type)
        {
            int bucket = type.IsUnion ? Union : type.IsNever ? Never : (int)type.Kind;
            return bucket * Width;
        }

        private void Record(NativeType type, int operation, int previousFlags)
        {
            int index = Index(type);
            int written = type.Machine?.ByteSize ?? 1;
            Interlocked.Increment(ref counts[index + operation]);
            Interlocked.Add(ref counts[index + Flags], written);
            Interlocked.Add(ref counts[index + Avoided], previousFlags - written);
        }

        internal void Collect(List<RuntimeProfileSample> samples)
        {
            long opens = Interlocked.Exchange(ref pendingOpens, 0), resizes = Interlocked.Exchange(ref pendingResizes, 0);
            long bytes = Interlocked.Exchange(ref pendingBytes, 0), hits = Interlocked.Exchange(ref pendingHits, 0);
            long misses = Interlocked.Exchange(ref pendingMisses, 0), adds = Interlocked.Exchange(ref pendingAdds, 0);
            long replaces = Interlocked.Exchange(ref pendingReplaces, 0), retired = Interlocked.Exchange(ref pendingRetired, 0);
            if ((opens | resizes | hits | misses | adds | replaces | retired) != 0)
                samples.Add(new("vm.storage.pending_index", new() { ["storage.owner"] = "native" }, new()
                {
                    ["owners_opened"] = (ulong)opens, ["owners_resized"] = (ulong)resizes,
                    ["reserved_bytes"] = (ulong)bytes, ["lookup_hits"] = (ulong)hits, ["lookup_misses"] = (ulong)misses,
                    ["slots_added"] = (ulong)adds, ["slots_replaced"] = (ulong)replaces, ["slots_retired"] = (ulong)retired
                }));

            for (int bucket = 0; bucket < Buckets; ++bucket)
            {
                int index = bucket * Width;
                long writes = Interlocked.Exchange(ref counts[index + Writes], 0);
                long copies = Interlocked.Exchange(ref counts[index + Copies], 0);
                long clears = Interlocked.Exchange(ref counts[index + Clears], 0);
                long flags = Interlocked.Exchange(ref counts[index + Flags], 0);
                long avoided = Interlocked.Exchange(ref counts[index + Avoided], 0);
                long pendingCopies = Interlocked.Exchange(ref counts[index + PendingCopies], 0);
                if (writes + copies + clears + pendingCopies == 0) continue;
                string kind = bucket == Union ? "Union" : bucket == Never ? "Never" : ((NativeKind)bucket).ToString();
                samples.Add(new("vm.storage.initialization", new()
                {
                    ["storage.kind"] = kind,
                    ["storage.policy"] = bucket == (int)NativeKind.MachineInteger || bucket == (int)NativeKind.MachineFloat
                        ? "machine-byte-extent" : "source-start",
                    ["attribution.scope"] = "runtime-profile"
                }, new()
                {
                    ["writes"] = (ulong)writes, ["copies"] = (ulong)copies, ["clears"] = (ulong)clears,
                    ["pending_copies"] = (ulong)pendingCopies,
                    ["flags_written"] = (ulong)flags, ["flags_avoided_vs_full_extent"] = (ulong)avoided
                }));
            }
        }
    }
}
