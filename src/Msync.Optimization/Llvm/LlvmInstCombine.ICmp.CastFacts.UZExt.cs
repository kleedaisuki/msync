// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp processUZExtIdiom.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? ProcessUZExtIdiom(LlvmICmpInstruction compare, LlvmValue value, LlvmAPInt other)
        {
            Count("icmp.uzext-overflow.queries");
            if (value.Type is not LlvmIntegerType) return null;
            var binary = (LlvmBinaryOperator)value;
            var left = (LlvmCastInstruction)binary.Left;
            var right = (LlvmCastInstruction)binary.Right;
            LlvmValue a = left.Operand, b = right.Operand;
            uint wa = a.Type.GetScalarSizeInBits(), wb = b.Type.GetScalarSizeInBits(), width = Math.Max(wa, wb);
            LlvmType type = wb > wa ? b.Type : a.Type;
            if (value.HasNUsesOrMore(2))
            {
                foreach (LlvmUser user in value.Users)
                {
                    if (user == compare) continue;
                    if (user is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
                    { if (trunc.Type.GetScalarSizeInBits() > width) return null; }
                    else if (user is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } || mask.Value.GetActiveBits() > width)
                        return null;
                }
            }
            bool match = compare.Predicate switch
            {
                LlvmCmpPredicate.ICmpUgt => other == LlvmAPInt.GetMaxValue(width).ZExt(other.BitWidth),
                LlvmCmpPredicate.ICmpUlt => other == LlvmAPInt.GetOneBitSet(other.BitWidth, width),
                _ => false
            };
            if (!match) return null;
            Builder.SetInsertPoint(binary);
            if (wa < width) a = Builder.CreateZExt(a, type);
            if (wb < width) b = Builder.CreateZExt(b, type);
            LlvmValue arithmetic, overflow;
            if (binary.Opcode == LlvmOpcode.Add)
            {
                arithmetic = Builder.CreateAdd(a, b, "add");
                overflow = Builder.CreateICmp(compare.Predicate == LlvmCmpPredicate.ICmpUlt ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUlt, arithmetic, a);
                Count("icmp.uzext-overflow.add");
            }
            else
            {
                LlvmValue call = Builder.CreateIntrinsic(LlvmIntrinsicKind.UMulWithOverflow, [a, b], type, "umul");
                arithmetic = Builder.CreateExtractValue(call, 0, "umul.value");
                overflow = Builder.CreateExtractValue(call, 1, "umul.overflow");
                if (compare.Predicate == LlvmCmpPredicate.ICmpUlt) overflow = Builder.CreateNot(overflow);
                Count("icmp.uzext-overflow.mul");
            }
            Worklist.Add(binary);
            if (value.HasNUsesOrMore(2))
            {
                foreach (LlvmUser user in value.Users.ToArray())
                {
                    if (user == compare) continue;
                    if (user is LlvmCastInstruction trunc)
                    {
                        if (trunc.Type.GetScalarSizeInBits() == width) ReplaceInstUsesWith(trunc, arithmetic);
                        else trunc.SetOperand(0, arithmetic);
                    }
                    else
                    {
                        var and = (LlvmBinaryOperator)user;
                        LlvmAPInt mask = ((LlvmConstantInt)and.Right).Value.Trunc(width);
                        LlvmValue narrowed = Builder.CreateAnd(arithmetic, LlvmConstantInt.Get(type, mask));
                        ReplaceInstUsesWith(and, Builder.CreateZExt(narrowed, and.Type));
                    }
                    Worklist.Add((LlvmInstruction)user);
                    Count("icmp.uzext-overflow.narrowed-users");
                }
            }
            return ReplaceInstUsesWith(compare, overflow);
        }
    }
}
