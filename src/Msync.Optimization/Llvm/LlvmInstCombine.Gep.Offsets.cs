// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp EmitGEPOffset/EmitGEPOffsets.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue EmitGEPOffset(LlvmGetElementPtrInstruction gep, bool rewriteGep = false) =>
            EmitGEPOffset(LlvmGepOperator.Get(gep), rewriteGep);

        private LlvmValue EmitGEPOffset(LlvmGepOperator gep, bool rewriteGep = false)
        {
            Count("gep.offset.emit");
            if (!rewriteGep) return LlvmGepUtils.EmitGEPOffset(Builder, DL, gep, metrics: Metrics);
            using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            var instruction = gep.User as LlvmInstruction;
            if (instruction is not null) Builder.SetInsertPoint(instruction);
            LlvmValue offset = EmitGEPOffset(gep);
            if (instruction is not null && !gep.HasAllConstantIndices && !gep.SourceElementType.IsIntegerTy(8))
            {
                ReplaceInstUsesWith(instruction, Builder.CreateGep(LlvmType.GetInt8Ty(gep.Context), gep.PointerOperand,
                    [offset], noWrapFlags: gep.NoWrapFlags));
                EraseInstFromFunction(instruction);
                Count("gep.offset.rewrites");
            }
            return offset;
        }

        private LlvmValue EmitGEPOffsets(IReadOnlyList<LlvmGepOperator> geps, LlvmGepNoWrapFlags flags,
            LlvmType indexType, bool rewriteGeps = false)
        {
            LlvmValue? sum = null, oneUseSum = null, oneUseBase = null;
            LlvmGepNoWrapFlags oneUseFlags = LlvmGepNoWrapFlags.All;
            for (int index = geps.Count - 1; index >= 0; --index)
            {
                LlvmGepOperator gep = geps[index];
                LlvmValue offset;
                using (new LlvmTransformBuilder.InsertPointGuard(Builder))
                {
                    var instruction = gep.User as LlvmInstruction;
                    if (rewriteGeps && instruction is not null) Builder.SetInsertPoint(instruction);
                    offset = LlvmGepUtils.EmitGEPOffset(Builder, DL, gep, metrics: Metrics);
                    if (!ReferenceEquals(offset.Type, indexType))
                        offset = Builder.CreateVectorSplat(((LlvmVectorType)indexType).ElementCount, offset);
                    if (gep.HasOneUse)
                    {
                        oneUseSum = Add(oneUseSum, offset);
                        oneUseFlags = oneUseFlags.IntersectForOffsetAdd(gep.NoWrapFlags);
                        oneUseBase ??= gep.PointerOperand;
                        continue;
                    }
                    if (oneUseSum is not null) offset = Add(oneUseSum, offset);
                    if (rewriteGeps && instruction is not null && offset.Type.IsVectorTy() == gep.Type.IsVectorTy() &&
                        !(gep.SourceElementType.IsIntegerTy(8) && ReferenceEquals(gep.GetIndex(0), offset)))
                    {
                        ReplaceInstUsesWith(instruction, Builder.CreatePtrAdd(oneUseBase ?? gep.PointerOperand, offset,
                            noWrapFlags: oneUseFlags.IntersectForOffsetAdd(gep.NoWrapFlags)));
                        EraseInstFromFunction(instruction);
                        Count("gep.offset.rewrites");
                    }
                }
                sum = Add(sum, offset);
                oneUseSum = oneUseBase = null;
                oneUseFlags = LlvmGepNoWrapFlags.All;
            }
            if (oneUseSum is not null) sum = Add(sum, oneUseSum);
            return sum ?? LlvmConstant.GetNullValue(indexType);

            LlvmValue Add(LlvmValue? current, LlvmValue offset) => current is null ? offset :
                Builder.CreateAdd(current, offset, nuw: flags.HasNoUnsignedWrap, nsw: flags.IsInBounds);
        }
    }
}
