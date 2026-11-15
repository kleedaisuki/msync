// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp foldLoadOfSelectOfConstPtrs/visitLoadInst.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldLoadOfSelectOfConstPtrs(LlvmLoadInstruction load)
        {
            var pointer = load.PointerOperand;
            var basis = pointer is LlvmGetElementPtrInstruction gep ? gep.PointerOperand : pointer;
            if (basis is not LlvmSelectInstruction { TrueValue: LlvmConstant whenTrue, FalseValue: LlvmConstant whenFalse } select) return null;
            var offset = LlvmAPInt.GetZero(DL.GetIndexTypeSizeInBits(pointer.Type));
            if (LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, DL, ref offset,
                allowNonInbounds: true, metrics: Metrics) != basis) return null;
            if (DL.GetIndexTypeSizeInBits(basis.Type) != offset.BitWidth) return null;
            var trueValue = LlvmConstantFolding.ConstantFoldLoadFromConstPtr(whenTrue, load.Type, offset, DL, Metrics);
            if (trueValue is null) return null;
            var falseValue = LlvmConstantFolding.ConstantFoldLoadFromConstPtr(whenFalse, load.Type, offset, DL, Metrics);
            if (falseValue is null) return null;
            Count("instcombine.memory.load.constant_select");
            return new LlvmSelectInstruction(select.Condition, trueValue, falseValue) { LogicalType = load.LogicalType };
        }

        private LlvmInstruction? VisitLoadInst(LlvmLoadInstruction load)
        {
            Count("instcombine.memory.load.visits");
            var pointer = load.PointerOperand;
            if (LlvmInstructionSimplify.SimplifyLoadInst(load, pointer, Query(load)) is { } simplified)
                return ReplaceInstUsesWith(load, simplified);
            if (CombineLoadToOperationType(load) is { } retyped) return retyped;
            if (ReplaceGepIdxWithZero(pointer, load) is { } gep) return ReplaceOperand(load, 0, gep);
            if (UnpackLoadToAggregate(load) is { } unpacked) return unpacked;

            var batch = new LlvmBatchAAResults(AA);
            if (LlvmLoads.FindAvailableLoadedValue(load, batch, out bool isLoadCse, Metrics) is { } available)
            {
                // AA metadata graphs are not represented. The source metadata CSE
                // intersection has no fields to merge in this carrier.
                Count(isLoadCse ? "instcombine.memory.load.cse" : "instcombine.memory.load.forwarded");
                return ReplaceInstUsesWith(load, Builder.CreateBitOrPointerCast(available, load.Type, load.Name + ".cast"));
            }
            if (!load.IsUnordered) return null;
            if (CanSimplifyNullLoadOrGep(load, pointer))
            {
                CreateNonTerminatorUnreachable(load);
                Count("instcombine.memory.load.unreachable");
                return ReplaceInstUsesWith(load, LlvmPoisonValue.Get(load.Type));
            }
            if (pointer.HasOneUse)
            {
                var addressCast = pointer as LlvmCastInstruction;
                if (addressCast?.Opcode != LlvmOpcode.AddrSpaceCast) addressCast = null;
                var selectOperand = addressCast is not null && addressCast.Operand.HasOneUse ? addressCast.Operand : pointer;
                if (selectOperand is LlvmSelectInstruction select &&
                    LlvmLoads.IsSafeToLoadUnconditionally(select.TrueValue, load.Type, load.Alignment, Query(select)) &&
                    LlvmLoads.IsSafeToLoadUnconditionally(select.FalseValue, load.Type, load.Alignment, Query(select)))
                {
                    var truePointer = addressCast is null ? select.TrueValue : Builder.CreateCast(
                        LlvmOpcode.AddrSpaceCast, select.TrueValue, addressCast.Type, select.TrueValue.Name + ".cast");
                    var trueLoad = Builder.Insert(new LlvmLoadInstruction(load.Type, truePointer, load.Alignment,
                        load.IsVolatile, load.Ordering, load.SyncScopeId) { IsElementwise = load.IsElementwise, LogicalType = load.LogicalType }, truePointer.Name + ".val");
                    trueLoad.GetOperandUse(0).LogicalType = load.GetOperandUse(0).LogicalType;
                    var falsePointer = addressCast is null ? select.FalseValue : Builder.CreateCast(
                        LlvmOpcode.AddrSpaceCast, select.FalseValue, addressCast.Type, select.FalseValue.Name + ".cast");
                    var falseLoad = Builder.Insert(new LlvmLoadInstruction(load.Type, falsePointer, load.Alignment,
                        load.IsVolatile, load.Ordering, load.SyncScopeId) { IsElementwise = load.IsElementwise, LogicalType = load.LogicalType }, falsePointer.Name + ".val");
                    falseLoad.GetOperandUse(0).LogicalType = load.GetOperandUse(0).LogicalType;
                    Count("instcombine.memory.load.select_speculated");
                    return new LlvmSelectInstruction(select.Condition, trueLoad, falseLoad) { LogicalType = load.LogicalType };
                }
            }
            if (FoldLoadOfSelectOfConstPtrs(load) is { } foldedSelect) return foldedSelect;
            if (!LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(pointer)) &&
                SimplifyNonNullOperand(pointer, true) is { } nonNull) return ReplaceOperand(load, 0, nonNull);
            // protected_field_ptr/ptrauth_auth have no registered intrinsic IDs.
            return null;
        }
    }
}
