using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<int, int[]> futureFieldsByLayout = graph.FutureFields.GroupBy(field => field.Layout)
            .ToDictionary(group => group.Key, group => group.Select(field => field.Ordinal).Order().ToArray());
        private readonly Dictionary<LlvmValue, int?> futureStorage = [];

        internal bool HasFutureFields => futureFieldsByLayout.Count != 0;

        private int OriginalExtent(int logical) =>
            Math.Max(1, checked((int)Data.GetTypeAllocSize(structs[logical]).GetFixedValue()));

        private int[] FutureRootOffsets(int logical)
        {
            if (!futureFieldsByLayout.TryGetValue(logical, out int[]? ordinals)) return [];
            int first = AlignWord(OriginalExtent(logical));
            return Enumerable.Range(0, ordinals.Length).Select(index => checked(first + index * sizeof(ulong))).ToArray();
        }

        private static int AlignWord(int size) => checked(size + 7) & -8;

        internal int? FutureStorageDelta(LlvmUse address)
        {
            if (!HasFutureFields) return null;
            // A folded zero GEP leaves its declared layout on the memory use.
            if (address.TargetStorageType is { } type && graph.StructLayouts.TryGetValue(type, out int layout))
                return TailAtPayloadOffset(layout, 0);
            return FutureStorageDelta(address.Value!);
        }

        private int? FutureStorageDelta(LlvmValue address)
        {
            if (!HasFutureFields) return null;
            if (futureStorage.TryGetValue(address, out int? cached)) return cached;
            int? delta = KnownFutureStorage(address);
            futureStorage.Add(address, delta);
            return delta;
        }

        private int? KnownFutureStorage(LlvmValue address)
        {
            switch (address)
            {
                case LlvmArgument argument when graph.FutureCaptures.TryGetValue(argument, out var fields):
                    int delta = FieldTailDelta(fields[0]);
                    if (fields.Any(field => FieldTailDelta(field) != delta))
                        throw new MsyncException($"HPIR capture {argument.ArgNo} requires separate payload/future addresses for different layouts");
                    return delta;
                case LlvmHeapAllocInst heap:
                    return TailAtPayloadOffset(heap.LogicalLayout, 0);
                case LlvmGlobalVariable global:
                    return TailAtPayloadOffset(GlobalLayout(global), 0);
                case LlvmGetElementPtrInstruction { HasAllConstantIndices: true } gep:
                    return GepTailDelta(gep);
                case LlvmConstantExpr constant
                    when LlvmGepOperator.TryGet(constant, out var gep) && gep.HasAllConstantIndices
                        && gep.PointerOperand is LlvmGlobalVariable root:
                    int offset = checked((int)Data.GetIndexedOffsetInType(gep.SourceElementType, gep.Indices.ToArray()));
                    return TailAtPayloadOffset(GlobalLayout(root), offset);
                default:
                    return null;
            }
        }

        private int? GepTailDelta(LlvmGetElementPtrInstruction gep)
        {
            int offset = checked((int)Data.GetIndexedOffsetInType(gep.SourceElementType, gep.Indices.ToArray()));
            if (gep.GetTargetStorageType() is { } type && graph.StructLayouts.TryGetValue(type, out int layout))
                return TailAtPayloadOffset(layout, offset);
            if (gep.PointerOperand is LlvmHeapAllocInst heap)
                return TailAtPayloadOffset(heap.LogicalLayout, offset);
            if (gep.PointerOperand is LlvmGlobalVariable global)
                return TailAtPayloadOffset(GlobalLayout(global), offset);
            return offset == 0 ? FutureStorageDelta(gep.PointerOperand) : null;
        }

        private int? TailAtPayloadOffset(int layout, int offset)
        {
            if (!futureFieldsByLayout.TryGetValue(layout, out var fields)) return null;
            var structure = structs[layout];
            foreach (int field in fields)
                if (FieldOffset(structure, (uint)field) == offset) return FieldTailDelta(new(layout, field));
            return null;
        }

        private int FieldTailDelta(LlvmFieldCarrier field)
        {
            int logical = field.Layout;
            int index = Array.BinarySearch(futureFieldsByLayout[logical], field.Ordinal);
            int root = checked(AlignWord(OriginalExtent(logical)) + index * sizeof(ulong));
            return root - FieldOffset(structs[logical], (uint)field.Ordinal);
        }
    }
}
