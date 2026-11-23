using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private readonly Dictionary<LlvmValue, int> frameAddresses = [];

        private bool PrepareConstantAddress(LlvmConstantExpr address)
        {
            if (!address.Type.IsPointerTy() || address.GetOperand(0) is not LlvmConstant parent) return false;
            long delta;
            if (LlvmGepOperator.TryGet(address, out var gep) && gep.HasAllConstantIndices)
                delta = Layout.GetIndexedOffsetInType(gep.SourceElementType, gep.Indices.ToArray());
            else if (address.Opcode is LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast && parent.Type.IsPointerTy()) delta = 0;
            else return false;
            PrepareConstant(parent);
            if (!operands.TryGetValue(parent, out int origin)) return false;
            if (delta == 0) operands.Add(address, origin);
            else
            {
                int output = Reserve(8, 8);
                Add(new(HpirOp.OffsetPointer, output, origin, Immediate: delta));
                operands.Add(address, output);
                ++constants;
            }
            return true;
        }

        private void Allocate(LlvmHeapAllocInst allocation)
        {
            int import = geometry.AllocationImport(allocation);
            EmitCall(allocation, ~import, Location(allocation), []);
        }

        private readonly Dictionary<LlvmAllocaInstruction, int[]> frameRootOffsets = [];

        private void PrepareFrame()
        {
            foreach (var instruction in function.BasicBlocks[0].Instructions)
            {
                if (instruction is not LlvmAllocaInstruction { IsStaticAlloca: true } allocation) continue;
                var size = allocation.GetAllocationSize(Layout);
                if (size is null || size.Value.IsScalable || allocation.Alignment.Value > 8)
                    throw Error("non-fixed stack allocation", allocation);
                int bytes = checked((int)size.Value.GetFixedValue());
                int alignment = (int)Math.Max(allocation.Alignment.Value, Layout.GetABITypeAlign(allocation.AllocatedType).Value);
                if (alignment > 8) throw Error("overaligned stack allocation", allocation);
                int offset = Reserve(Math.Max(1, bytes), alignment);
                storage.Add(allocation, offset);
                if (IsCompletionItem(allocation))
                {
                    int completion = Reserve(8, 8);
                    frameCompletions.Add(allocation, completion);
                    Add(new(HpirOp.ConstU64, completion));
                }
                var roots = new List<int>();
                int stride = checked((int)allocation.GetAllocationBaseSize(Layout).GetFixedValue());
                if (stride != 0)
                {
                    foreach (var leaf in geometry.Leaves(allocation.AllocatedType))
                    {
                        if (!geometry.IsGuestPointer(leaf.Type)) continue;
                        for (int element = 0; element < bytes; element += stride)
                        {
                            int root = checked(offset + element + leaf.Offset);
                            roots.Add(root);
                            Add(new(HpirOp.ConstU64, root));
                        }
                    }
                }
                roots.Sort();
                frameRootOffsets.Add(allocation, roots.ToArray());
            }
        }

        private void RecordAddress(LlvmInstruction address, int offset)
        {
            frameAddresses.Add(address, offset);
            ++staticAddresses;
            foreach (var use in address.Uses)
            {
                if (use.User is LlvmLoadInstruction load && ReferenceEquals(load.PointerOperand, address)
                    || use.User is LlvmStoreInstruction store && ReferenceEquals(store.PointerOperand, address)
                        && !ReferenceEquals(store.ValueOperand, address)
                    || use.User is LlvmGetElementPtrInstruction { HasAllConstantIndices: true } gep
                        && ReferenceEquals(gep.PointerOperand, address))
                    continue;
                Add(new(HpirOp.AddressOf, Location(address), offset), address.Source);
                break;
            }
        }

        private void Address(LlvmGetElementPtrInstruction gep)
        {
            long delta = Layout.GetIndexedOffsetInType(gep.SourceElementType, gep.Indices.ToArray());
            if (frameAddresses.TryGetValue(gep.PointerOperand, out int offset))
            {
                RecordAddress(gep, checked((int)(offset + delta)));
                return;
            }
            int pointer = Location(gep.PointerOperand);
            if (delta == 0) Alias(gep, pointer);
            else Add(new(HpirOp.OffsetPointer, Location(gep), pointer, Immediate: delta), gep.Source);
        }

        private void DynamicAddress(LlvmGetElementPtrInstruction gep)
        {
            LlvmType element = gep.SourceElementType;
            int from = Location(gep.PointerOperand);
            int destination = Location(gep);
            long constant = 0;
            for (int position = 0; position < gep.IndexCount; ++position)
            {
                LlvmValue index = gep.GetIndex(position);
                if (position != 0 && element is LlvmStructType structure && index is LlvmConstantInt field)
                {
                    uint ordinal = checked((uint)field.GetZExtValue());
                    constant = unchecked(constant + geometry.FieldOffset(structure, ordinal));
                    element = structure.Elements[(int)ordinal];
                    continue;
                }
                if (position != 0 && element is LlvmArrayType array) element = array.ElementType;
                else if (position != 0) throw Error("non-scalar address displacement", gep);
                long stride = checked((long)Layout.GetTypeAllocSize(element).GetFixedValue());
                if (index is LlvmConstantInt immediate)
                {
                    constant = unchecked(constant + immediate.GetSExtValue() * stride);
                    continue;
                }
                if (stride == 0) continue;
                int term = ScaledDisplacement(index, stride, gep);
                Add(new(HpirOp.AddPointer, destination, from, term), gep.Source);
                from = destination;
            }
            if (constant != 0)
                Add(new(HpirOp.OffsetPointer, destination, from, Immediate: constant), gep.Source);
            else if (from != destination) Copy(HpirWidth.U64, destination, from, gep.Source);
        }

        private int displacement = -1, scale = -1;

        private int ScaledDisplacement(LlvmValue index, long stride, LlvmInstruction site)
        {
            int term = Location(index);
            if (index.Type is not LlvmIntegerType integer) throw Error("non-integer pointer displacement", site);
            if (integer.BitWidth != 64)
            {
                if (displacement < 0) displacement = Reserve(8, 8);
                SignExtend(integer, term, displacement, site);
                term = displacement;
            }
            if (stride == 1) return term;
            if (displacement < 0) displacement = Reserve(8, 8);
            if (scale < 0) scale = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, scale, Immediate: stride), site.Source);
            Add(new(HpirOp.MulU64, displacement, term, scale), site.Source);
            return displacement;
        }

        private int Memory(LlvmValue pointer)
        {
            if (frameAddresses.TryGetValue(pointer, out int direct)) return direct;
            ++indirectAccesses;
            return ~Location(pointer);
        }
    }
}
