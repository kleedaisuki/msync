// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitGEPOfGEP.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitGEPOfGEP(LlvmGetElementPtrInstruction gep, LlvmGepOperator source)
        {
            if (!GepShouldMerge(LlvmGepOperator.Get(gep), source)) return null;
            if (CanonicalizeGEPOfConstGEPI8(gep, source) is { } canonicalized) return canonicalized;
            if (CombineGEPConstantOffsets(gep) is { } combined) return combined;
            if (!ReferenceEquals(source.ResultElementType, gep.SourceElementType)) return null;
            if (source.HasOneUse && gep.IndexCount == 1 && source.IndexCount == 1 &&
                FoldGEPConstantSelect(gep, source) is { } select) return select;

            bool sequential = false;
            LlvmType current = source.SourceElementType;
            for (int index = 0; index < source.IndexCount; ++index)
            {
                sequential = index == 0 || current is not LlvmStructType;
                if (index > 0) current = LlvmGetElementPtrInstruction.GetTypeAtIndex(current, source.GetIndex(index))!;
            }
            if (!sequential) return null;
            LlvmValue sourceIndex = source.GetIndex(source.IndexCount - 1), gepIndex = gep.GetIndex(0);
            if (!ReferenceEquals(sourceIndex.Type, gepIndex.Type)) return null;
            LlvmValue? sum = LlvmInstructionSimplify.SimplifyAddInst(gepIndex, sourceIndex, false, false, Query(gep));
            if (sum is null) return null;
            List<LlvmValue> indices = [];
            for (int index = 0; index + 1 < source.IndexCount; ++index) indices.Add(source.GetIndex(index));
            indices.Add(sum);
            for (int index = 1; index < gep.IndexCount; ++index) indices.Add(gep.GetIndex(index));
            uint nonzero = 0;
            foreach (LlvmValue index in indices)
                if (index is not LlvmConstant { IsNullValue: true }) ++nonzero;
            if (nonzero > 1) return null;
            Count("gep.chained_indices_combined");
            return ReplaceInstUsesWith(gep, Builder.CreateGep(source.SourceElementType, source.PointerOperand, indices,
                noWrapFlags: source.NoWrapFlags.IntersectForOffsetAdd(gep.NoWrapFlags)));
        }

        private LlvmInstruction? FoldGEPConstantSelect(LlvmGetElementPtrInstruction gep, LlvmGepOperator source)
        {
            LlvmValue sourceIndex = source.GetIndex(0), gepIndex = gep.GetIndex(0);
            LlvmConstantInt? offset = GepAPInt(sourceIndex);
            var select = gepIndex as LlvmSelectInstruction;
            if (offset is null || select is null || GepAPInt(select.TrueValue) is null || GepAPInt(select.FalseValue) is null)
            {
                offset = GepAPInt(gepIndex);
                select = sourceIndex as LlvmSelectInstruction;
            }
            if (offset is null || select is not { HasOneUse: true } ||
                GepAPInt(select.TrueValue) is not { } whenTrue || GepAPInt(select.FalseValue) is not { } whenFalse ||
                whenTrue.BitWidth != offset.BitWidth || whenFalse.BitWidth != offset.BitWidth) return null;
            LlvmConstant trueValue = GepConstantInt(select.Type, offset.Value + whenTrue.Value);
            LlvmConstant falseValue = GepConstantInt(select.Type, offset.Value + whenFalse.Value);
            LlvmValue selected = Builder.CreateSelect(select.Condition, trueValue, falseValue);
            if (selected is LlvmInstruction newSelect) newSelect.Source = select.Source;
            Count("gep.chained_constant_select_folded");
            return ReplaceInstUsesWith(gep, Builder.CreateGep(gep.ResultElementType, source.PointerOperand, [selected],
                noWrapFlags: source.NoWrapFlags.IntersectForOffsetAdd(gep.NoWrapFlags)));
        }

        private static LlvmConstant GepConstantInt(LlvmType type, LlvmAPInt value)
        {
            LlvmConstant scalar = LlvmConstantInt.Get(type.Context, value);
            return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
        }
    }
}
