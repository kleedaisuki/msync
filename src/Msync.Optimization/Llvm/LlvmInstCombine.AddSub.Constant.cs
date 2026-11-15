// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// foldAddWithConstant; scalar m_ImmConstant excludes ConstantExpr.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAddWithConstant(LlvmBinaryOperator add)
        {
            if (add.Right is not LlvmConstant constant || !IsAddSubImmediateConstant(constant)) return null;
            if (FoldBinOpIntoSelectOrPhi(add) is { } threaded) return threaded;
            if (FoldBinOpSelectBinOp(add) is { } selected) return selected;
            LlvmValue left = add.Left;
            LlvmType type = add.Type;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstant first } subtract)
                return new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantExpr.GetAdd(first, constant), subtract.Right);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true } sub && constant is LlvmConstantInt { IsMinusOne: true })
                return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateNot(sub.Right), sub.Left);
            var one = LlvmConstantInt.Get(type.Context, new LlvmAPInt(type.GetScalarSizeInBits(), 1));
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } cast && cast.Operand.Type.GetScalarSizeInBits() == 1)
                return CreateSelectInstWithUnknownProfile(cast.Operand,
                    cast.Opcode == LlvmOpcode.ZExt ? LlvmConstantExpr.GetAdd(constant, one) : LlvmConstantExpr.GetSub(constant, one), constant);
            if (Negator.MatchNot(left, out var notOperand))
                return new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantExpr.GetSub(constant, one), notOperand)
                { HasNoSignedWrap = add.HasNoSignedWrap && WillNotOverflowSignedSub(constant, one, add) };
            uint width = type.GetScalarSizeInBits();
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, HasOneUse: true, Right: LlvmConstantInt shift } shifted &&
                shift.Value == new LlvmAPInt(width, width - 1) && constant is LlvmConstantInt { IsOne: true })
                return new LlvmCastInstruction(LlvmOpcode.ZExt,
                    Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, shifted.Left, LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(width))), type);
            if (constant is not LlvmConstantInt c) return null;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, Right: LlvmConstant inner } disjoint && IsAddSubImmediateConstant(inner))
                return new LlvmBinaryOperator(LlvmOpcode.Add, disjoint.Left, LlvmConstantExpr.GetAdd(inner, constant))
                { HasNoSignedWrap = add.HasNoSignedWrap && WillNotOverflowSignedAdd(inner, constant, add), HasNoUnsignedWrap = add.HasNoUnsignedWrap };
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt mask } && mask.Value == -c.Value)
                return new LlvmBinaryOperator(LlvmOpcode.Xor, left, mask);
            if (c.Value.IsMinSignedValue)
                return add.HasNoSignedWrap || add.HasNoUnsignedWrap ?
                    new LlvmBinaryOperator(LlvmOpcode.Or, left, constant) { IsDisjoint = true } :
                    new LlvmBinaryOperator(LlvmOpcode.Xor, left, constant);
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt sign } narrowXor } &&
                sign.Value.IsMinSignedValue && sign.Value.SExt(width) == c.Value)
                return new LlvmCastInstruction(LlvmOpcode.SExt, narrowXor.Left, type);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt xorConstant } xor)
            {
                LlvmValue x = xor.Left;
                if (xorConstant.Value.IsMinSignedValue)
                    return new LlvmBinaryOperator(LlvmOpcode.Add, x, LlvmConstantInt.Get(type.Context, xorConstant.Value ^ c.Value));
                if ((xorConstant.Value | ComputeKnownBits(x, add).Zero).IsAllOnes)
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantInt.Get(type.Context, xorConstant.Value + c.Value), x);
                if (left.HasOneUse && xorConstant.Value == -c.Value)
                {
                    uint shiftBy = c.Value.IsPowerOf2 ? width - c.Value.GetActiveBits() :
                        xorConstant.Value.IsPowerOf2 ? width - xorConstant.Value.GetActiveBits() : 0;
                    if (shiftBy != 0 && MaskedValueIsZero(x, LlvmAPInt.GetHighBitsSet(width, shiftBy), add))
                    {
                        var amount = LlvmConstantInt.Get(type.Context, new LlvmAPInt(width, shiftBy));
                        return new LlvmBinaryOperator(LlvmOpcode.AShr, Builder.CreateBinary(LlvmOpcode.Shl, x, amount), amount);
                    }
                }
            }
            if (left is LlvmCallInstruction { HasOneUse: true } call && call.ArgumentCount == 2 &&
                call.GetArgument(1) is LlvmConstantInt callConstant && callConstant.Value == -c.Value)
            {
                var kind = call.CalledFunction?.IntrinsicDescriptor?.Kind;
                if (kind == LlvmIntrinsicKind.UMax)
                    return ReplaceInstUsesWith(add, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.USubSat, call.GetArgument(0), callConstant));
                if (kind == LlvmIntrinsicKind.UAddSat)
                    return ReplaceInstUsesWith(add, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UMin, call.GetArgument(0),
                        LlvmConstantInt.Get(type.Context, ~callConstant.Value)));
            }
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt narrowC } narrowAdd })
            {
                uint narrowWidth = narrowC.Value.BitWidth;
                if (c.Value.GetActiveBits() <= narrowWidth)
                {
                    var narrowed = c.Value.Trunc(narrowWidth);
                    if (narrowC.Value == -narrowed && (narrowed.IsOne ? IsKnownNonZero(narrowAdd.Left, add) :
                        ComputeKnownBits(narrowAdd.Left, add).GetMinValue().Uge(narrowed)))
                        return new LlvmCastInstruction(LlvmOpcode.ZExt, narrowAdd.Left, type);
                }
            }
            return null;
        }
    }
}
