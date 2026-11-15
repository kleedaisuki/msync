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
        private const int Union = (int)NativeKind.MachineFloat + 1, Never = Union + 1, Buckets = Never + 1, Width = 5;
        private const int Writes = 0, Copies = 1, Clears = 2, Flags = 3, Avoided = 4;
        private readonly long[] counts = new long[Buckets * Width];

        internal void Write(NativeType type) => Record(type, Writes, Math.Max(1, type.Representation.Bytes));
        internal void Clear(NativeType type) => Record(type, Clears, Math.Max(1, type.Representation.Bytes));
        internal void Copy(NativeType type) => Record(type, Copies, Math.Max(1, type.Representation.Bytes));
        internal void CopyKind(NativeType source, NativeType destination, NativeKind kind)
        {
            int bytes = source.Machine?.ByteSize ?? NativeRepresentation.Of(kind).Bytes;
            Record(destination, Copies, Math.Max(1, bytes) + (destination.IsUnion ? 1 : 0));
        }
        internal void Scalar(NativeType type, NativeKind kind) => Record(type, Writes,
            (kind == NativeKind.Number ? 8 : 1) + (type.Representation.PayloadOffset != 0 ? 1 : 0));
        internal void Address(NativeType type) => Record(type, Writes, 8);

        private void Record(NativeType type, int operation, int previousFlags)
        {
            int bucket = type.IsUnion ? Union : type.IsNever ? Never : (int)type.Kind;
            int index = bucket * Width;
            int written = type.Machine?.ByteSize ?? 1;
            Interlocked.Increment(ref counts[index + operation]);
            Interlocked.Add(ref counts[index + Flags], written);
            Interlocked.Add(ref counts[index + Avoided], previousFlags - written);
        }

        internal void Collect(List<RuntimeProfileSample> samples)
        {
            for (int bucket = 0; bucket < Buckets; ++bucket)
            {
                int index = bucket * Width;
                long writes = Interlocked.Exchange(ref counts[index + Writes], 0);
                long copies = Interlocked.Exchange(ref counts[index + Copies], 0);
                long clears = Interlocked.Exchange(ref counts[index + Clears], 0);
                long flags = Interlocked.Exchange(ref counts[index + Flags], 0);
                long avoided = Interlocked.Exchange(ref counts[index + Avoided], 0);
                if (writes + copies + clears == 0) continue;
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
                    ["flags_written"] = (ulong)flags, ["flags_avoided_vs_full_extent"] = (ulong)avoided
                }));
            }
        }
    }
}
