// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldLogicOfFCmps, matchIsFiniteTest, foldLogicOfIsFPClass, reassociateFCmps;
// InstCombiner.h stripSignOnlyFPOps.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue StripSignOnlyFPOps(LlvmValue value)
        {
            if (value is LlvmUnaryOperator { Opcode: LlvmOpcode.FNeg } neg) value = neg.Operand;
            else if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP { IsZero: true } zero } sub &&
                (zero.IsNegative || (sub.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0)) value = sub.Right;
            if (BoolCmpIntrinsic(value, LlvmIntrinsicKind.FAbs, out var abs)) value = abs.GetArgument(0);
            if (BoolCmpIntrinsic(value, LlvmIntrinsicKind.CopySign, out var sign)) value = sign.GetArgument(0);
            return value;
        }
        private static bool BoolCmpFloatZero(LlvmValue value, bool positiveOnly = false)
            => value is LlvmConstantFP constant && constant.IsZero && (!positiveOnly || !constant.IsNegative);
        private static bool BoolCmpUnordered(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.FCmpUno and <= LlvmCmpPredicate.FCmpUne;
        private LlvmValue? MatchIsFiniteTest(LlvmFCmpInstruction left, LlvmFCmpInstruction right)
        {
            if (left.Predicate != LlvmCmpPredicate.FCmpOrd || !BoolCmpFloatZero(left.Right) ||
                !BoolCmpUnordered(right.Predicate) || right.Right is not LlvmConstantFP { Value.IsInfinity: true }) return null;
            return Builder.CreateFCmp((LlvmCmpPredicate)((byte)right.Predicate & 7), right.Left, right.Right,
                fastMathFlags: left.FastMathFlags & right.FastMathFlags);
        }
        private LlvmValue? FoldLogicOfFCmps(LlvmFCmpInstruction left, LlvmFCmpInstruction right, bool isAnd, bool logical)
        {
            LlvmValue left0 = left.Left, left1 = left.Right, right0 = right.Left, right1 = right.Right;
            LlvmCmpPredicate predLeft = left.Predicate, predRight = right.Predicate;
            if (left0 == right1 && right0 == left1)
            { predRight = LlvmCmpInstruction.GetSwappedPredicate(predRight); (right0, right1) = (right1, right0); }
            if (left0 == right0 && left1 == right1)
            {
                uint code = isAnd ? LlvmCmpInstAnalysis.GetFCmpCode(predLeft) & LlvmCmpInstAnalysis.GetFCmpCode(predRight) :
                    LlvmCmpInstAnalysis.GetFCmpCode(predLeft) | LlvmCmpInstAnalysis.GetFCmpCode(predRight);
                return BoolCmpRewrite(GetFCmpValue(code, left0, left1, left.FastMathFlags & right.FastMathFlags), "fcmp_same_operands");
            }
            if ((predLeft == LlvmCmpPredicate.FCmpOrd && predRight == LlvmCmpPredicate.FCmpOrd && isAnd ||
                 predLeft == LlvmCmpPredicate.FCmpUno && predRight == LlvmCmpPredicate.FCmpUno && !isAnd))
            {
                if (left0.Type != right0.Type) return null;
                if (BoolCmpFloatZero(left1, true) && BoolCmpFloatZero(right1, true))
                {
                    LlvmValue other = right0;
                    LlvmFastMathFlags flags = left.FastMathFlags & right.FastMathFlags;
                    if (logical)
                    {
                        other = Builder.CreateFreeze(other);
                        BoolCmpAppendName(other, right0, ".fr"u8);
                        flags &= ~(LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs);
                    }
                    return BoolCmpRewrite(Builder.CreateFCmp(predLeft, left0, other, fastMathFlags: flags), "fcmp_nan_pair");
                }
            }
            if (!logical && isAnd && StripSignOnlyFPOps(left0) == StripSignOnlyFPOps(right0))
            {
                if (MatchIsFiniteTest(left, right) is { } finite) return BoolCmpRewrite(finite, "fcmp_finite");
                if (MatchIsFiniteTest(right, left) is { } otherFinite) return BoolCmpRewrite(otherFinite, "fcmp_finite_commuted");
            }
            if (left.HasOneUse && right.HasOneUse)
            {
                var (classRight, maskRight) = LlvmKnownFPClasses.FCmpToClassTest(predRight, F, right0, right1, metrics: Metrics);
                if (classRight is not null)
                {
                    var (classLeft, maskLeft) = LlvmKnownFPClasses.FCmpToClassTest(predLeft, F, left0, left1, metrics: Metrics);
                    if (classLeft == classRight)
                        return BoolCmpRewrite(Builder.CreateIsFPClass(classLeft!, isAnd ? maskLeft & maskRight : maskLeft | maskRight), "fcmp_class_pair");
                }
            }
            if (left0 == right0 && left.HasOneUse && right.HasOneUse && LlvmCmpInstruction.GetSwappedPredicate(predLeft) == predRight &&
                left1 is LlvmConstantFP lc && right1 is LlvmConstantFP rc && lc.Value.BitcastToAPInt() == rc.Value.ChangeSign().BitcastToAPInt())
            {
                bool Less(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle;
                LlvmAPFloat leftConstant = lc.Value, rightConstant = rc.Value;
                if (Less(isAnd ? predRight : predLeft))
                { (leftConstant, rightConstant) = (rightConstant, leftConstant); (predLeft, predRight) = (predRight, predLeft); }
                if (Less(isAnd ? predLeft : predRight))
                {
                    LlvmFastMathFlags flags = left.FastMathFlags;
                    if (!logical) flags |= right.FastMathFlags;
                    LlvmValue magnitude = Builder.CreateFAbs(left0, flags);
                    return BoolCmpRewrite(Builder.CreateFCmp(predLeft, magnitude, LlvmConstantFP.Get(left0.Type, leftConstant), fastMathFlags: flags), "fcmp_absolute_range");
                }
            }
            return null;
        }
        private bool MatchIsFPClassLikeFCmp(LlvmValue value, out LlvmValue classValue, out LlvmFPClassTest mask)
        {
            if (value is LlvmFCmpInstruction { HasOneUse: true } compare)
            {
                var result = LlvmKnownFPClasses.FCmpToClassTest(compare.Predicate, F, compare.Left, compare.Right, metrics: Metrics);
                if (result.Value is not null) { classValue = result.Value; mask = result.Classes; return true; }
            }
            classValue = null!; mask = default; return false;
        }
        private static bool BoolCmpFPClassIntrinsic(LlvmValue value, out LlvmValue classValue, out LlvmFPClassTest mask)
        {
            if (value.HasOneUse && BoolCmpIntrinsic(value, LlvmIntrinsicKind.IsFPClass, out var call) &&
                call.GetArgument(1) is LlvmConstantInt constant)
            { classValue = call.GetArgument(0); mask = (LlvmFPClassTest)constant.GetZExtValue(); return true; }
            classValue = null!; mask = default; return false;
        }
        private LlvmInstruction? FoldLogicOfIsFPClass(LlvmBinaryOperator operation, LlvmValue left, LlvmValue right)
        {
            bool leftIntrinsic = BoolCmpFPClassIntrinsic(left, out var leftClass, out var leftMask);
            bool rightIntrinsic = BoolCmpFPClassIntrinsic(right, out var rightClass, out var rightMask);
            if (!(leftIntrinsic || MatchIsFPClassLikeFCmp(left, out leftClass, out leftMask)) ||
                !(rightIntrinsic || MatchIsFPClassLikeFCmp(right, out rightClass, out rightMask)) || leftClass != rightClass) return null;
            LlvmFPClassTest mask = operation.Opcode switch
            { LlvmOpcode.And => leftMask & rightMask, LlvmOpcode.Or => leftMask | rightMask, LlvmOpcode.Xor => leftMask ^ rightMask, _ => throw new InvalidOperationException() };
            Count("boolean_compare.rewrite.fpclass_logic");
            if (leftIntrinsic || rightIntrinsic)
            {
                var intrinsic = (LlvmCallInstruction)(leftIntrinsic ? left : right);
                intrinsic.SetArgument(1, LlvmConstantInt.Get((LlvmIntegerType)intrinsic.GetArgument(1).Type, (uint)mask));
                return ReplaceInstUsesWith(operation, intrinsic);
            }
            return ReplaceInstUsesWith(operation, Builder.CreateIsFPClass(leftClass, mask));
        }
        private LlvmInstruction? ReassociateFCmps(LlvmBinaryOperator operation)
        {
            LlvmValue first = operation.Left, second = operation.Right;
            if (second is LlvmFCmpInstruction cmp && BoolCmpFloatZero(cmp.Right)) (first, second) = (second, first);
            LlvmCmpPredicate predicate = operation.Opcode == LlvmOpcode.And ? LlvmCmpPredicate.FCmpOrd : LlvmCmpPredicate.FCmpUno;
            if (first is not LlvmFCmpInstruction left || left.Predicate != predicate || !BoolCmpFloatZero(left.Right) ||
                second is not LlvmBinaryOperator inner || inner.Opcode != operation.Opcode) return null;
            LlvmValue inner0 = inner.Left, inner1 = inner.Right;
            bool Match(LlvmValue candidate) => candidate is LlvmFCmpInstruction comparison && comparison.Predicate == predicate &&
                BoolCmpFloatZero(comparison.Right) && left.Left.Type == comparison.Left.Type;
            if (!Match(inner0)) (inner0, inner1) = (inner1, inner0);
            if (!Match(inner0)) return null;
            var right = (LlvmFCmpInstruction)inner0;
            LlvmValue combined = Builder.CreateFCmp(predicate, left.Left, right.Left, fastMathFlags: left.FastMathFlags & right.FastMathFlags);
            Count("boolean_compare.rewrite.reassociate_nan_checks");
            return new LlvmBinaryOperator(operation.Opcode, combined, inner1);
        }
    }
}
