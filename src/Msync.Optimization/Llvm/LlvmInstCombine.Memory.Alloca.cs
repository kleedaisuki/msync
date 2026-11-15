// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp simplifyAllocaArraySize/visitAllocaInst.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SimplifyAllocaArraySize(LlvmAllocaInstruction alloca)
        {
            if (alloca.ArraySize is LlvmConstantInt { IsOne: true })
            {
                if (alloca.ArraySize.Type.IsIntegerTy(32)) return null;
                Count("instcombine.memory.alloca.scalar_count");
                return ReplaceOperand(alloca, 0, LlvmConstantInt.Get(LlvmType.GetInt32Ty(alloca.Context), 1));
            }
            if (alloca.ArraySize is LlvmConstantInt constant && constant.Value.GetActiveBits() <= 64)
            {
                var type = LlvmArrayType.Get(alloca.AllocatedType, constant.GetZExtValue());
                var replacement = Builder.Insert(new LlvmAllocaInstruction(type,
                    LlvmConstantInt.Get(LlvmType.GetInt32Ty(alloca.Context), 1), alloca.AddressSpace, alloca.Alignment)
                    { InAlloca = alloca.InAlloca }, alloca.Name);
                Count("instcombine.memory.alloca.array_type");
                return ReplaceInstUsesWith(alloca, replacement);
            }
            if (alloca.ArraySize is LlvmUndefValue)
                return ReplaceInstUsesWith(alloca, LlvmPoisonValue.Get(alloca.Type));
            var indexType = DL.GetIndexType(alloca.Context, alloca.AddressSpace);
            if (alloca.ArraySize.Type == indexType) return null;
            Count("instcombine.memory.alloca.index_type");
            return ReplaceOperand(alloca, 0, Builder.CreateIntCast(alloca.ArraySize, indexType, false));
        }

        private LlvmInstruction? VisitAllocaInst(LlvmAllocaInstruction alloca)
        {
            Count("instcombine.memory.alloca.visits");
            if (SimplifyAllocaArraySize(alloca) is { } simplified) return simplified;
            var size = LlvmValueTracking.GetAllocationSize(alloca, DL);
            if (size is { IsZero: true })
            {
                if (alloca.ArraySize is not LlvmConstantInt { IsOne: true })
                    return ReplaceOperand(alloca, 0, LlvmConstantInt.Get((LlvmIntegerType)alloca.ArraySize.Type, 1));
                var first = F.BasicBlocks[0].Instructions.First(instruction => instruction is not LlvmPhiInstruction);
                if (first != alloca)
                {
                    var entry = first as LlvmAllocaInstruction;
                    var entrySize = entry is null ? null : LlvmValueTracking.GetAllocationSize(entry, DL);
                    if (entrySize is not { IsZero: true })
                    {
                        alloca.RemoveFromParent();
                        first.Parent!.InsertBefore(alloca, first);
                        Count("instcombine.memory.alloca.zero_size_move");
                        return alloca;
                    }
                    entry!.Alignment = new(Math.Max(entry.Alignment.Value, alloca.Alignment.Value));
                    Count("instcombine.memory.alloca.zero_size_merge");
                    return ReplaceInstUsesWith(alloca, entry);
                }
            }
            var toDelete = new List<LlvmInstruction>();
            if (IsOnlyCopiedFromConstantMemory(alloca, toDelete) is { } copy)
            {
                var source = LlvmValueTracking.StripPointerCasts(copy.GetArgument(1));
                var alignment = LlvmLocal.GetOrEnforceKnownAlignment(source, alloca.Alignment, DL,
                    alloca, AC, DT, Metrics);
                if (alloca.Alignment.Value <= alignment.Value && IsDereferenceableForAllocaSize(source, alloca) &&
                    source is not LlvmInstruction)
                {
                    if (alloca.AddressSpace == MemoryAddressSpace(source))
                    {
                        foreach (var instruction in toDelete) EraseInstFromFunction(instruction);
                        var result = ReplaceInstUsesWith(alloca, source);
                        EraseInstFromFunction(copy);
                        Count("instcombine.memory.constant_copy.eliminated");
                        return result;
                    }
                    var replacer = new MemoryPointerReplacer(this, alloca, MemoryAddressSpace(source));
                    if (replacer.CollectUsers())
                    {
                        foreach (var instruction in toDelete) EraseInstFromFunction(instruction);
                        replacer.ReplacePointer(source);
                        Count("instcombine.memory.constant_copy.cross_address_space");
                    }
                }
            }
            return VisitAllocSite(alloca);
        }
    }
}
