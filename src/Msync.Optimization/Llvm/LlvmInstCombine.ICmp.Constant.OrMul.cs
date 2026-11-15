// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 2089-2382.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldICmpOrXorSubChain(LlvmICmpInstruction compare, LlvmBinaryOperator or)
        {
            Count("icmp.constant.or-chain.queries");
            List<(LlvmValue Left, LlvmValue Right)> comparisons = [];
            Stack<LlvmValue> pending = new(); pending.Push(or);
            while (pending.TryPop(out var current))
            {
                Count("icmp.constant.or-chain.visited");
                if (current is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } operation) return null;
                Add(operation.Right); Add(operation.Left);
            }
            var last = comparisons[^1];
            LlvmValue result = Builder.CreateICmp(compare.Predicate, last.Left, last.Right);
            for (int i = comparisons.Count - 2; i >= 0; --i)
                result = Builder.CreateBinary(compare.Predicate == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.And : LlvmOpcode.Or,
                    result, Builder.CreateICmp(compare.Predicate, comparisons[i].Left, comparisons[i].Right));
            Count("icmp.constant.or-chain.rewrites");
            return result;
            void Add(LlvmValue value)
            {
                if (value is LlvmBinaryOperator { HasOneUse: true } binary && binary.Opcode is LlvmOpcode.Xor or LlvmOpcode.Sub)
                    comparisons.Add((binary.Left, binary.Right));
                else pending.Push(value);
            }
        }

        private LlvmInstruction? FoldICmpOrConstant(LlvmICmpInstruction compare, LlvmBinaryOperator or, LlvmAPInt constant)
        {
            Count("icmp.constant.or.queries");
            LlvmCmpPredicate predicate = compare.Predicate;
            if (constant.IsOne && predicate == LlvmCmpPredicate.ICmpSlt && IccMatchSignum(or) is { } signum)
                return IccCompare(predicate, signum, IccConstant(signum, 1));
            LlvmValue x = or.Left, y = or.Right;
            if (ICmpIsEquality(predicate) && y is LlvmConstant && or.IsDisjoint)
                return IccCompare(predicate, x, Builder.CreateXor(y, IccConstant(y, constant)));
            if (y is LlvmConstantInt mask && ICmpIsEquality(predicate))
            {
                if (mask.Value == constant && (constant + IccInt(constant, 1)).IsPowerOf2)
                    return IccCompare(predicate == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUle : LlvmCmpPredicate.ICmpUgt, x, y);
                if (or.HasOneUse)
                {
                    Count("icmp.constant.or.clear-mask");
                    return IccCompare(predicate, Builder.CreateAnd(x, IccConstant(x, ~mask.Value)), IccConstant(or, constant ^ mask.Value));
                }
            }
            if (LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative))
            {
                LlvmValue? input = Predecessor(x, y) ?? Predecessor(y, x);
                if (input is not null) return IccCompare(negative ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSgt, input, IccConstant(input, negative ? 1UL : 0));
            }
            if (constant.IsNonNegative && y is LlvmConstantInt orConstant)
            {
                if (predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge && orConstant.Value.Sge(constant))
                    return IccCompare(predicate, x, IccConstant(x, 0));
                if (predicate is LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSgt && orConstant.Value.Sgt(constant))
                    return IccCompare(predicate == LlvmCmpPredicate.ICmpSle ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge, x, IccConstant(x, 0));
            }
            if (!ICmpIsEquality(predicate) || !constant.IsZero || !or.HasOneUse) return null;
            if (x is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } p && y is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } q)
                return new LlvmBinaryOperator(predicate == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.And : LlvmOpcode.Or,
                    Builder.CreateICmp(predicate, p.Operand, LlvmConstant.GetNullValue(p.Operand.Type)),
                    Builder.CreateICmp(predicate, q.Operand, LlvmConstant.GetNullValue(q.Operand.Type)));
            if (FoldICmpOrXorSubChain(compare, or) is { } chain) return ReplaceInstUsesWith(compare, chain);
            return null;
            static LlvmValue? Predecessor(LlvmValue a, LlvmValue b) => a is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { Value.IsAllOnes: true } } add && ReferenceEquals(add.Left, b) ? b : null;
        }

        private static LlvmCmpPredicate IccStrictPredicate(LlvmCmpPredicate predicate) => predicate switch
        {
            LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUlt, LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
            LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt, LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt, _ => predicate
        };

        private LlvmInstruction? FoldICmpMulConstant(LlvmICmpInstruction compare, LlvmBinaryOperator mul, LlvmAPInt constant)
        {
            Count("icmp.constant.mul.queries");
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue input = mul.Left;
            if (ReferenceEquals(input, mul.Right) && !ICmpIsSigned(predicate))
            {
                Count("icmp.constant.mul.sqrt-query");
                LlvmAPInt root = constant.SqrtFloor();
                bool square = constant == root * root;
                if (ICmpIsEquality(predicate) && (mul.HasNoUnsignedWrap || mul.HasNoSignedWrap && constant.IsZero))
                {
                    if (!square) return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context, predicate == LlvmCmpPredicate.ICmpNe));
                    return New(predicate, root, "square-equality");
                }
                if (mul.HasNoUnsignedWrap)
                {
                    if (square) return New(predicate, root, "square-exact");
                    if (predicate is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle) root += IccInt(root, 1);
                    return New(IccStrictPredicate(predicate), root, "square-rounded");
                }
            }
            if (mul.Right is not LlvmConstantInt multiplier) return null;
            LlvmAPInt factor = multiplier.Value;
            if (ICmpIsSignTest(ref predicate, constant) && mul.HasNoSignedWrap)
            {
                if (factor.IsNegative) predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                return New(predicate, LlvmAPInt.GetZero(constant.BitWidth), "sign-test");
            }
            if (factor.IsZero) return null;
            if (ICmpIsEquality(predicate))
            {
                if (mul.HasNoSignedWrap && constant.SRem(factor).IsZero) return New(predicate, constant.SDiv(factor), "signed-exact-division");
                if (constant.URem(factor).IsZero && ((factor & IccInt(factor, 1)).IsOne || mul.HasNoUnsignedWrap))
                    return New(predicate, constant.UDiv(factor), "unsigned-exact-division");
            }
            if (mul.HasNoSignedWrap && ICmpIsSigned(predicate))
            {
                if (constant.IsMinSignedValue && factor.IsAllOnes) return null;
                if (factor.IsNegative) predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                var rounding = predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge ? LlvmAPIntRounding.Up : LlvmAPIntRounding.Down;
                return New(predicate, LlvmAPInt.RoundingSDiv(constant, factor, rounding), "signed-rounded-division");
            }
            if (mul.HasNoUnsignedWrap && ICmpIsUnsigned(predicate))
            {
                var rounding = predicate is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge ? LlvmAPIntRounding.Up : LlvmAPIntRounding.Down;
                return New(predicate, LlvmAPInt.RoundingUDiv(constant, factor, rounding), "unsigned-rounded-division");
            }
            return null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.mul." + reason); return IccCompare(pred, input, IccConstant(input, rhs)); }
        }
    }
}
