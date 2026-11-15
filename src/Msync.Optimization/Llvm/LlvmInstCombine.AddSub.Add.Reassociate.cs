// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp visitAdd patterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAddSubNotOffset(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction, bool oneOnly)
        {
            for (int side = 0; side != 2; ++side)
            {
                var addValue = side == 0 ? left : right;
                var other = side == 0 ? right : left;
                if (addValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt constant } add ||
                    (oneOnly ? !constant.IsOne : !add.HasOneUse) || !Negator.MatchNot(other, out var b)) continue;
                LlvmValue difference = oneOnly ? new LlvmBinaryOperator(LlvmOpcode.Sub, add.Left, b) : Builder.CreateSub(add.Left, b);
                return oneOnly ? (LlvmInstruction)difference : new LlvmBinaryOperator(LlvmOpcode.Add, difference,
                    LlvmConstantInt.Get(constant.Context, constant.Value - new LlvmAPInt(constant.Value.BitWidth, 1)));
            }
            if (right is not LlvmConstantInt c || (oneOnly ? !c.IsOne : !left.HasOneUse) || left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add } inner) return null;
            LlvmValue a;
            LlvmValue not;
            if (Negator.MatchNot(inner.Left, out not)) a = inner.Right;
            else if (Negator.MatchNot(inner.Right, out not)) a = inner.Left;
            else return null;
            if (oneOnly) return new LlvmBinaryOperator(LlvmOpcode.Sub, a, not);
            return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateSub(a, not), LlvmConstantInt.Get(c.Context, c.Value - new LlvmAPInt(c.Value.BitWidth, 1)));
        }
        private LlvmInstruction? FoldAddSubSeparatedConstants(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue first = side == 0 ? left : right, second = side == 0 ? right : left;
                if (first is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstant c1 } add && IsAddSubImmediateConstant(c1) &&
                    second is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstant c2 } sub && IsAddSubImmediateConstant(c2) && (left.HasOneUse || right.HasOneUse))
                    return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateSub(add.Left, sub.Right), LlvmConstantExpr.GetAdd(c1, c2));
            }
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue subValue = side == 0 ? left : right, other = side == 0 ? right : left;
                if (subValue is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true, Left: LlvmConstant c } sub && IsAddSubImmediateConstant(c))
                    return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateSub(other, sub.Right), c);
            }
            return null;
        }
        private LlvmInstruction? FoldAddSubAndSignBit(LlvmValue masked, LlvmValue value)
        {
            if (masked is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } and ||
                !ReferenceEquals(and.Left, value) || !mask.Value.IsPowerOf2 || ComputeNumSignBits(value) <= mask.Value.CountLeadingZeros()) return null;
            return new LlvmBinaryOperator(LlvmOpcode.And, value, LlvmConstantInt.Get(mask.Context, mask.Value - new LlvmAPInt(mask.Value.BitWidth, 1)));
        }
        private LlvmInstruction? FoldAddSubExtensions(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue first = side == 0 ? left : right, second = side == 0 ? right : left;
                if (first is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } a && second is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt,
                        Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true } sub } && ReferenceEquals(a.Operand, sub.Right))
                    return new LlvmCastInstruction(LlvmOpcode.ZExt, sub.Left, instruction.Type);
            }
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue first = side == 0 ? left : right, second = side == 0 ? right : left;
                if (first is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } a || second is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt } b) continue;
                if (ReferenceEquals(a.Operand, b.Operand) && a.Operand.Type.IsIntOrIntVectorTy(1)) return ReplaceInstUsesWith(instruction, AddSubInt(instruction, 0));
                if (b.Operand is not LlvmICmpInstruction less || a.Operand is not LlvmICmpInstruction greater || !less.Left.Type.IsIntOrIntVectorTy()) continue;
                var lt = less.Predicate; var gt = greater.Predicate;
                var x = less.Left; var y = less.Right;
                if (ReferenceEquals(greater.Left, y) && ReferenceEquals(greater.Right, x)) gt = LlvmICmpInstruction.GetSwappedPredicate(gt);
                else if (!ReferenceEquals(greater.Left, x) || !ReferenceEquals(greater.Right, y)) continue;
                if (lt is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpUgt) { (lt, gt) = (gt, lt); (x, y) = (y, x); }
                if ((lt == LlvmCmpPredicate.ICmpSlt && gt == LlvmCmpPredicate.ICmpSgt) || (lt == LlvmCmpPredicate.ICmpUlt && gt == LlvmCmpPredicate.ICmpUgt))
                    return ReplaceInstUsesWith(instruction, Builder.CreateIntrinsic(lt == LlvmCmpPredicate.ICmpSlt ? LlvmIntrinsicKind.SCmp : LlvmIntrinsicKind.UCmp, [x, y], instruction.Type));
            }
            return null;
        }
        private LlvmInstruction? FoldAddSubLogicPair(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                var first = (side == 0 ? left : right) as LlvmBinaryOperator;
                var second = side == 0 ? right : left;
                if (first is null || first.Opcode is not (LlvmOpcode.Xor or LlvmOpcode.Or) || second is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and) continue;
                bool same = ReferenceEquals(first.Left, and.Left) && ReferenceEquals(first.Right, and.Right) || ReferenceEquals(first.Left, and.Right) && ReferenceEquals(first.Right, and.Left);
                if (!same) continue;
                if (first.Opcode == LlvmOpcode.Xor) return new LlvmBinaryOperator(LlvmOpcode.Or, first.Left, first.Right);
                ReplaceOperand(instruction, 0, first.Left); ReplaceOperand(instruction, 1, first.Right); return instruction;
            }
            return null;
        }
        private LlvmInstruction? FoldAddSubNegatedOr(LlvmValue value, LlvmValue candidate, LlvmBinaryOperator instruction)
        {
            if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or ||
                !(ReferenceEquals(or.Left, value) && ReferenceEquals(AddSubNegated(or.Right), value) || ReferenceEquals(or.Right, value) && ReferenceEquals(AddSubNegated(or.Left), value))) return null;
            return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(value, LlvmConstantInt.Get(value.Context, LlvmAPInt.GetAllOnes(value.Type.GetScalarSizeInBits())),
                nuw: instruction.HasNoUnsignedWrap, nsw: instruction.HasNoSignedWrap), value);
        }
    }
}
