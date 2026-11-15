// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp isObjectSizeLessThanOrEq,
// canReplaceGEPIdxWithZero/replaceGEPIdxWithZero/simplifyNonNullOperand.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool IsObjectSizeLessThanOrEq(LlvmValue value, ulong maximum)
        {
            var visited = new HashSet<LlvmValue>();
            var pending = new Stack<LlvmValue>();
            pending.Push(value);
            do
            {
                var pointer = LlvmValueTracking.StripPointerCasts(pending.Pop());
                if (!visited.Add(pointer)) continue;
                Count("instcombine.memory.object_size.visits");
                if (pointer is LlvmSelectInstruction select)
                { pending.Push(select.TrueValue); pending.Push(select.FalseValue); continue; }
                if (pointer is LlvmPhiInstruction phi)
                {
                    for (int i = 0; i < phi.IncomingCount; ++i) pending.Push(phi.GetIncomingValue(i));
                    continue;
                }
                if (pointer is LlvmAllocaInstruction alloca)
                {
                    var size = LlvmValueTracking.GetAllocationSize(alloca, DL);
                    if (size is null || size.Value.IsScalable || size.Value.GetFixedValue() > maximum) return false;
                    continue;
                }
                if (pointer is LlvmGlobalVariable global)
                {
                    if (!global.HasDefinitiveInitializer || !global.IsConstant) return false;
                    if (DL.GetTypeAllocSize(global.ValueType).GetFixedValue() > maximum) return false;
                    continue;
                }
                // This source fold is intentionally limited to alloca and constant
                // globals. Fresh HeapAlloc identity is not static alloca identity.
                return false;
            } while (pending.Count != 0);
            return true;
        }

        private bool CanReplaceGepIdxWithZero(LlvmGetElementPtrInstruction gep, LlvmInstruction memory, out int index)
        {
            index = 1;
            if (gep.OperandCount < 2) return false;
            while (index != gep.OperandCount && gep.GetOperand(index) is LlvmConstantInt { IsZero: true }) ++index;
            if (index == gep.OperandCount || gep.GetOperand(index) is LlvmConstant) return false;
            if (gep.SourceElementType.IsScalableTy()) return false;
            var allocated = LlvmGetElementPtrInstruction.GetIndexedType(gep.SourceElementType, gep.Indices.Take(index).ToArray());
            if (allocated is null || !allocated.IsSized()) return false;
            ulong allocationSize = DL.GetTypeAllocSize(allocated).GetFixedValue();
            if (index + 1 != gep.OperandCount && !gep.NoWrapFlags.IsInBounds) return false;
            if (!IsObjectSizeLessThanOrEq(gep.PointerOperand, allocationSize)) return false;
            for (int i = index + 1; i < gep.OperandCount; ++i)
                if (!LlvmKnownBitsAnalysis.Compute(gep.GetOperand(i)!, context: memory, tree: DT,
                    metrics: Metrics, useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC,
                    allowEphemerals: SQ.AllowEphemerals, conditions: SQ.DC).IsNonNegative) return false;
            return true;
        }

        private LlvmInstruction? ReplaceGepIdxWithZero(LlvmValue pointer, LlvmInstruction memory)
        {
            if (pointer is not LlvmGetElementPtrInstruction gep || !CanReplaceGepIdxWithZero(gep, memory, out int index)) return null;
            var replacement = gep.Clone();
            replacement.SetOperand(index, LlvmConstantInt.Get((LlvmIntegerType)gep.GetOperand(index)!.Type, 0));
            InsertNewInstBefore(replacement, gep);
            if (gep.Parent == memory.Parent && LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(gep, memory, 32, Metrics))
            {
                ReplaceInstUsesWith(gep, replacement);
                EraseInstFromFunction(gep);
                Count("instcombine.memory.gep_zero.all_users");
            }
            Count("instcombine.memory.gep_zero.rewrites");
            return replacement;
        }

        private static uint MemoryAddressSpace(LlvmValue pointer) => ((LlvmPointerType)pointer.Type).AddressSpace;
        private bool CanSimplifyNullStoreOrGep(LlvmStoreInstruction store)
        {
            if (LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(store.PointerOperand))) return false;
            var pointer = store.PointerOperand;
            if (pointer is LlvmGetElementPtrInstruction gep) pointer = gep.PointerOperand;
            return pointer is LlvmConstantPointerNull;
        }
        private bool CanSimplifyNullLoadOrGep(LlvmLoadInstruction load, LlvmValue pointer)
        {
            if (pointer is LlvmGetElementPtrInstruction { PointerOperand: LlvmConstantPointerNull } gep &&
                !LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(gep.PointerOperand))) return true;
            return pointer is LlvmUndefValue || pointer is LlvmConstantPointerNull &&
                !LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(load.PointerOperand));
        }

        private LlvmValue? SimplifyNonNullOperand(LlvmValue value, bool useProvenance, uint depth = 0)
        {
            Count("instcombine.memory.nonnull.visits");
            if (value is LlvmSelectInstruction select)
            {
                if (select.TrueValue is LlvmConstantPointerNull) return select.FalseValue;
                if (select.FalseValue is LlvmConstantPointerNull) return select.TrueValue;
            }
            if (!value.HasOneUse || depth == 3) return null;
            if (value is LlvmGetElementPtrInstruction gep && (useProvenance ||
                gep.NoWrapFlags.IsInBounds && !LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(gep.PointerOperand))))
            {
                if (SimplifyNonNullOperand(gep.PointerOperand, useProvenance, depth + 1) is { } result)
                { ReplaceOperand(gep, 0, result); Worklist.Add(gep); Count("instcombine.memory.nonnull.gep"); }
            }
            if (value is LlvmPhiInstruction phi)
            {
                bool changed = false;
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    if (SimplifyNonNullOperand(phi.GetIncomingValue(i), useProvenance, 3) is not { } result) continue;
                    ReplaceUse(phi.GetOperandUse(i), result);
                    changed = true;
                }
                if (changed) { Worklist.Add(phi); Count("instcombine.memory.nonnull.phi"); }
            }
            return null;
        }
    }
}
