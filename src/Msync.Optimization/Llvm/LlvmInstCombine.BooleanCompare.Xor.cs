// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp foldXorOfICmps.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldXorOfICmps(LlvmICmpInstruction left, LlvmICmpInstruction right, LlvmBinaryOperator operation)
        {
            Debug.Assert(operation.Opcode == LlvmOpcode.Xor && operation.Left == left && operation.Right == right);
            LlvmCmpPredicate predLeft = left.Predicate, predRight = right.Predicate;
            LlvmValue left0 = left.Left, left1 = left.Right, right0 = right.Left, right1 = right.Right;
            if (LlvmCmpInstAnalysis.PredicatesFoldable(predLeft, predRight))
            {
                if (left0 == right1 && left1 == right0)
                { (left0, left1) = (left1, left0); predLeft = LlvmCmpInstruction.GetSwappedPredicate(predLeft); }
                if (left0 == right0 && left1 == right1)
                    return BoolCmpRewrite(GetNewICmpValue(LlvmCmpInstAnalysis.GetICmpCode(predLeft) ^ LlvmCmpInstAnalysis.GetICmpCode(predRight),
                        BoolCmpSigned(left.Predicate) || BoolCmpSigned(right.Predicate), left0, left1), "xor_comparison_same_operands");
            }
            if (left1 is LlvmConstantInt lc && right1 is LlvmConstantInt rc && left0.Type == right0.Type && left0.Type.IsIntOrIntVectorTy())
            {
                if ((left.HasOneUse || right.HasOneUse) && LlvmValueTracking.IsSignBitCheck(predLeft, lc.Value, out bool negativeLeft) &&
                    LlvmValueTracking.IsSignBitCheck(predRight, rc.Value, out bool negativeRight))
                {
                    LlvmValue xor = Builder.CreateXor(left0, right0);
                    return BoolCmpRewrite(Builder.CreateICmp(negativeLeft == negativeRight ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge,
                        xor, BoolCmpC(xor, 0)), "xor_sign_checks");
                }
                if (left0 == right0)
                {
                    LlvmConstantRange first = LlvmConstantRange.MakeExactICmpRegion(predLeft, lc.Value);
                    LlvmConstantRange second = LlvmConstantRange.MakeExactICmpRegion(predRight, rc.Value);
                    if (first.ExactUnionWith(second) is { } union && first.ExactIntersectWith(second) is { } intersection &&
                        union.ExactIntersectWith(intersection.Inverse()) is { } symmetric)
                    {
                        if (symmetric.IsFullSet) return LlvmConstantInt.GetTrue(operation.Context);
                        if (symmetric.IsEmptySet) return LlvmConstantInt.GetFalse(operation.Context);
                        symmetric.GetEquivalentICmp(out var predicate, out var constant, out var offset);
                        if (offset.IsZero && (left.HasOneUse || right.HasOneUse) || left.HasOneUse && right.HasOneUse)
                        {
                            LlvmValue value = left0;
                            if (!offset.IsZero) value = Builder.CreateAdd(value, BoolCmpC(value, offset));
                            return BoolCmpRewrite(Builder.CreateICmp(predicate, value, BoolCmpC(value, constant)), "xor_interval_difference");
                        }
                    }
                }
                if (BoolCmpEq(predLeft) && BoolCmpEq(predRight) && lc.IsZero && rc.IsZero && left.HasOneUse && right.HasOneUse &&
                    left0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } firstMask &&
                    right0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } secondMask && firstMask.Right == secondMask.Right &&
                    BoolCmpPower(firstMask.Right, true, operation))
                {
                    LlvmValue xor = Builder.CreateXor(firstMask.Left, secondMask.Left);
                    LlvmValue masked = Builder.CreateAnd(xor, firstMask.Right);
                    return BoolCmpRewrite(Builder.CreateICmp(predLeft == predRight ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq,
                        masked, BoolCmpC(xor, 0)), "xor_single_bit_tests");
                }
            }
            if (LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.Or, left, right, SQ) is { } orResult &&
                LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.And, left, right, SQ) is { } andResult)
            {
                LlvmICmpInstruction? x = null, y = null;
                if (orResult == left && andResult == right) { x = left; y = right; }
                if (orResult == right && andResult == left) { x = right; y = left; }
                if (x is not null && y is not null && (y.HasOneUse || CanFreelyInvertAllUsersOf(y, operation)))
                {
                    y.Predicate = LlvmCmpInstruction.GetInversePredicate(y.Predicate);
                    if (!y.HasOneUse)
                    {
                        var insertion = Builder.SaveInsertionPoint();
                        Builder.SetInsertPoint(y.Parent!, y.Next);
                        LlvmValue inverted = Builder.CreateNot(y);
                        BoolCmpAppendName(inverted, y, ".not"u8);
                        Worklist.PushUsersToWorkList(y);
                        foreach (LlvmUse use in y.Uses.ToArray()) if (use.User != inverted) use.Set(inverted);
                        Builder.RestoreInsertionPoint(insertion);
                    }
                    return BoolCmpRewrite(Builder.CreateAnd(left, right), "xor_implication_to_and");
                }
            }
            return null;
        }
        private static void BoolCmpAppendName(LlvmValue value, LlvmValue source, ReadOnlySpan<byte> suffix)
        {
            if (value is not LlvmInstruction instruction) return;
            byte[] name = new byte[source.NameBytes.Length + suffix.Length];
            source.NameBytes.Span.CopyTo(name); suffix.CopyTo(name.AsSpan(source.NameBytes.Length));
            instruction.SetName(name);
        }
    }
}
