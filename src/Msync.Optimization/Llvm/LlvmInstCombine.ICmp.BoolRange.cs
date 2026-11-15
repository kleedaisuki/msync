// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp foldICmpUsingBoolRange.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpUsingBoolRange(LlvmICmpInstruction compare)
        {
            Count("icmp.bool-range.queries");
            return Match(compare.Left, compare.Right, compare.Predicate) ??
                Match(compare.Right, compare.Left, LlvmCmpInstruction.GetSwappedPredicate(compare.Predicate));
            LlvmInstruction? Match(LlvmValue x, LlvmValue rhs, LlvmCmpPredicate pred)
            {
                if (rhs is not LlvmCastInstruction ext || ext.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt)) return null;
                LlvmValue y = ext.Operand;
                if (ext.HasOneUse && y.Type.IsIntOrIntVectorTy(1) &&
                    (ext.Opcode == LlvmOpcode.ZExt && pred == LlvmCmpPredicate.ICmpUlt || ext.Opcode == LlvmOpcode.SExt && pred == LlvmCmpPredicate.ICmpUle))
                {
                    Count("icmp.bool-range.null-test");
                    return new LlvmBinaryOperator(ext.Opcode == LlvmOpcode.ZExt ? LlvmOpcode.And : LlvmOpcode.Or,
                        Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, x, LlvmConstant.GetNullValue(x.Type)), y);
                }
                if (!ICmpIsEquality(pred) || y is not LlvmICmpInstruction inner || !ICmpIsEquality(inner.Predicate) ||
                    inner.Left != x || ICmpAPInt(inner.Right) is not { Value: var c }) return null;
                bool sext = ext.Opcode == LlvmOpcode.SExt;
                bool oneUse = ext.HasOneUse && inner.HasOneUse;
                if (c.IsZero || (sext ? c.IsAllOnes : c.IsOne))
                {
                    bool impossible = c.IsZero ? inner.Predicate == LlvmCmpPredicate.ICmpEq : inner.Predicate == LlvmCmpPredicate.ICmpNe;
                    if (impossible)
                    {
                        Count("icmp.bool-range.self-compare-constant");
                        return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Type, pred == LlvmCmpPredicate.ICmpNe));
                    }
                    if (sext && !oneUse) return null;
                    Count("icmp.bool-range.self-compare-range");
                    return new LlvmBinaryOperator(pred == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.Or : LlvmOpcode.And,
                        Builder.CreateICmp(pred, x, LlvmConstant.GetNullValue(x.Type)),
                        Builder.CreateICmp(pred, x, sext ? LlvmConstant.GetAllOnesValue(x.Type) :
                            LlvmConstantInt.Get(x.Type, new LlvmAPInt(x.Type.GetScalarSizeInBits(), 1))));
                }
                Count("icmp.bool-range.self-compare-reduced");
                LlvmConstant selected = inner.Predicate == LlvmCmpPredicate.ICmpEq ? LlvmConstant.GetNullValue(x.Type) :
                    sext ? LlvmConstant.GetAllOnesValue(x.Type) : LlvmConstantInt.Get(x.Type, new LlvmAPInt(x.Type.GetScalarSizeInBits(), 1));
                return IccCompare(pred, x, selected);
            }
        }
    }
}
