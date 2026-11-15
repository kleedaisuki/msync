// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: visitOr overflow extract patterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool LogicOverflowExtract(LlvmValue value, uint index, out LlvmCallBase call)
        {
            if (value is LlvmExtractValueInstruction { IndexCount: 1, AggregateOperand: LlvmCallBase candidate } extract
                && extract.Indices[0] == index && candidate.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsOverflowIntrinsic(descriptor.Kind))
            { call = candidate; return true; }
            call = null!; return false;
        }
        private LlvmInstruction? FoldOrOverflowPatterns(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var overflow = orientation == 0 ? i.Left : i.Right; var other = orientation == 0 ? i.Right : i.Left;
                if (!LogicOverflowExtract(overflow, 1, out var operation) || other is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe, Right: LlvmConstantInt { IsZero: true } } cmp
                    || !LogicOverflowExtract(cmp.Left, 0, out var result) || !ReferenceEquals(result, operation)
                    || operation.CalledFunction!.IntrinsicDescriptor!.Kind != LlvmIntrinsicKind.UMulWithOverflow || (!overflow.HasOneUse && (!other.HasOneUse || !cmp.Left.HasOneUse))) continue;
                var a = operation.GetArgument(0); var b = operation.GetArgument(1);
                var nonzeroA = Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, a, LogicZero(a.Type));
                var nonzeroB = Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, b, LogicZero(b.Type));
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, nonzeroA, nonzeroB), "or.umul_overflow_or_nonzero");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var overflow = orientation == 0 ? i.Left : i.Right; var other = orientation == 0 ? i.Right : i.Left;
                if (!LogicOverflowExtract(overflow, 1, out var operation) || other is not LlvmICmpInstruction { HasOneUse: true, Right: LlvmConstantInt bound } cmp
                    || !LogicOverflowExtract(cmp.Left, 0, out var result) || !ReferenceEquals(result, operation)
                    || operation.GetArgument(1) is not LlvmConstantInt c) continue;
                var kind = operation.CalledFunction!.IntrinsicDescriptor!.Kind;
                var opcode = LlvmIntrinsic.GetOverflowOpcode(kind);
                if (opcode is not (LlvmOpcode.Add or LlvmOpcode.Sub)) continue;
                bool equality = cmp.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
                bool signed = cmp.Predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
                if (!equality && (LlvmIntrinsic.GetOverflowNoWrapKind(kind) == 2) != signed) continue;
                bool hasOverflow;
                LlvmAPInt newConstant;
                if (opcode == LlvmOpcode.Add) newConstant = signed ? bound.Value.SSubOverflow(c.Value, out hasOverflow) : bound.Value.USubOverflow(c.Value, out hasOverflow);
                else newConstant = signed ? bound.Value.SAddOverflow(c.Value, out hasOverflow) : bound.Value.UAddOverflow(c.Value, out hasOverflow);
                if (hasOverflow && !equality) continue;
                var compare = Builder.CreateICmp(cmp.Predicate, operation.GetArgument(0), LogicC(operation.GetArgument(0).Type, newConstant));
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, overflow, compare), "or.overflow_result_bound");
            }
            return null;
        }
    }
}
