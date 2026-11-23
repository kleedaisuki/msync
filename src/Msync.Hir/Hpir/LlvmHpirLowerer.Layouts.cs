using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry(LlvmHirLoweringResult graph, Dictionary<LlvmFunction, int> entries)
    {
        internal LlvmDataLayout Data => graph.Module.DataLayout;
        private readonly IReadOnlyDictionary<int, LlvmStructType> structs = graph.PhysicalStructs;
        private readonly Dictionary<LlvmType, Leaf[]> leaves = [];
        private readonly Dictionary<int, int> layoutIds = graph.SessionGeneration?.HpirLayouts is { } previous
            ? new(previous.LogicalIds) : [];
        private readonly int inheritedLayoutCount = graph.SessionGeneration?.HpirLayouts?.Layouts.Count ?? 0;
        private int inheritedLayoutHits;
        private readonly Dictionary<int, int> allocationImports = [], layoutImports = [];
        private readonly Dictionary<LlvmFunction, long> identities = [];
        internal readonly List<HpirLayout> Layouts = graph.SessionGeneration?.HpirLayouts is { } previousLayouts
            ? new(previousLayouts.Layouts) : [];
        internal readonly List<HpirImport> Imports = [];

        internal readonly record struct Leaf(int Offset, LlvmType Type);

        internal Leaf[] Leaves(LlvmType type)
        {
            if (leaves.TryGetValue(type, out var cached)) return cached;
            var results = new List<Leaf>();
            Gather(type, 0, results);
            cached = results.ToArray();
            leaves.Add(type, cached);
            return cached;
        }

        private void Gather(LlvmType type, int start, List<Leaf> result)
        {
            switch (type)
            {
                case LlvmStructType structure when !structure.IsOpaque:
                    for (uint index = 0; index < structure.Elements.Count; ++index)
                        Gather(structure.Elements[(int)index], checked(start + FieldOffset(type, index)), result);
                    break;
                case LlvmArrayType array:
                    for (uint index = 0; index < array.NumElements; ++index)
                        Gather(array.ElementType, checked(start + FieldOffset(type, index)), result);
                    break;
                default:
                    if (!type.IsVoidTy()) result.Add(new(start, type));
                    break;
            }
        }

        internal int FieldOffset(LlvmType type, uint index) => type switch
        {
            LlvmStructType structure => checked((int)Data.GetStructLayout(structure).GetElementOffset(index).GetFixedValue()),
            LlvmArrayType array => checked((int)(Data.GetTypeAllocSize(array.ElementType).GetFixedValue() * index)),
            _ => throw new ArgumentException("HPIR field offset requires an aggregate carrier", nameof(type))
        };

        internal int FieldOffset(LlvmType type, IReadOnlyList<uint> path)
        {
            int result = 0;
            foreach (uint field in path)
            {
                result = checked(result + FieldOffset(type, field));
                type = type is LlvmStructType structure ? structure.Elements[(int)field] : ((LlvmArrayType)type).ElementType;
            }
            return result;
        }

        internal long CodeIdentity(LlvmFunction function)
        {
            if (graph.SessionGeneration is { } session)
            {
                if (graph.SourceFunctions.TryGetValue(function, out var source) && source.SourceFunction is { } id)
                    return session.Function(id) + 1L;
                if (graph.TemplateFunctions.TryGetValue(function, out var sourceTemplate) && sourceTemplate.SourceTemplate is { } template)
                    return session.Template(template) + 1L;
            }
            if (entries.TryGetValue(function, out int entry)) return entry + 1L;
            if (!identities.TryGetValue(function, out long identity))
            {
                identity = -1 - identities.Count;
                identities.Add(function, identity);
            }
            return identity;
        }

        internal int AllocationImport(LlvmHeapAllocInst allocation)
        {
            int layout = LayoutId(allocation.LogicalLayout, allocation.Source);
            if (allocation.ByteSize is not LlvmConstantInt count || count.GetZExtValue() != (ulong)OriginalExtent(allocation.LogicalLayout)
                || allocation.Alignment.Value > 16)
                throw new MsyncException("HPIR allocation and producer geometry disagree", allocation.Source);
            if (TrySessionAllocation(allocation, layout, out int sessionImport)) return sessionImport;
            if (!allocationImports.TryGetValue(layout, out int id))
            {
                id = Imports.Count;
                Imports.Add(new(HpirService.AllocateObject, layout, HpirWidth.U64, []));
                allocationImports.Add(layout, id);
            }
            return id;
        }

        internal bool TryLayoutImport(LlvmFunction target, SourceSpan? source, out int id)
        {
            id = -1;
            if (!graph.ValueOperations.TryGetValue(target, out var binding) || binding.Operation != LlvmRuntimeValueOperation.HasLayout)
                return false;
            id = LayoutImport(binding.Mode, source);
            return true;
        }

        private int LayoutImport(int logical, SourceSpan? source) => PhysicalLayoutImport(LayoutId(logical, source));

        private int PhysicalLayoutImport(int layout)
        {
            if (!layoutImports.TryGetValue(layout, out int id))
            {
                id = Imports.Count;
                Imports.Add(new(HpirService.HasLayout, layout, HpirWidth.U8, [HpirWidth.U64]));
                layoutImports.Add(layout, id);
            }
            return id;
        }

        private int LayoutId(int logical, SourceSpan? source)
        {
            if (layoutIds.TryGetValue(logical, out int id))
            {
                if (id < inheritedLayoutCount) ++inheritedLayoutHits;
                return id;
            }
            if (!structs.TryGetValue(logical, out var structure))
                throw new MsyncException("HPIR requires a concrete producer layout", source);
            id = Layouts.Count;
            int[] exact = FutureRootOffsets(logical);
            int size = exact.Length == 0 ? OriginalExtent(logical) : checked(exact[^1] + sizeof(ulong));
            int alignment = Math.Max(exact.Length == 0 ? 1 : sizeof(ulong),
                checked((int)Data.GetABITypeAlign(structure).Value));
            Layouts.Add(new(size, alignment, exact)
            {
                BorrowOffsets = Leaves(structure).Where(leaf => IsGuestPointer(leaf.Type)).Select(leaf => leaf.Offset).ToArray()
            });
            layoutIds.Add(logical, id);
            RememberSessionScopeLayout(logical, id);
            return id;
        }

        internal bool IsGuestPointer(LlvmType type) =>
            type is LlvmPointerType ptr && ptr.AddressSpace != Data.GetProgramAddressSpace();

        internal bool HasProvenLayout(LlvmValue value, IReadOnlyList<int> domain) => value switch
        {
            LlvmHeapAllocInst allocation => domain.Contains(allocation.LogicalLayout),
            LlvmGlobalVariable global => domain.Contains(GlobalLayout(global)),
            _ => false
        };
    }
}
