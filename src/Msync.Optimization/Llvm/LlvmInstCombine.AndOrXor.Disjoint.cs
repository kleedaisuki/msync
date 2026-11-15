// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp 3880–4082.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private readonly record struct BitmaskProduct(LlvmValue X, LlvmAPInt Factor, LlvmAPInt Mask, bool NUW, bool NSW);
        private BitmaskProduct? MatchBitmaskMul(LlvmValue value)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And } and } mul &&
                FunnelAPInt(and.Right) is { } mask && FunnelAPInt(mul.Right) is { } factor)
            {
                if (factor.Value.IsZero || mask.Value.IsZero) return null;
                return new(and.Left, factor.Value, mask.Value, mul.HasNoUnsignedWrap, mul.HasNoSignedWrap);
            }
            if (value is not LlvmSelectInstruction select || FunnelAPInt(select.TrueValue) is not { } trueValue ||
                FunnelAPInt(select.FalseValue) is not { } falseValue ||
                LlvmCmpInstAnalysis.DecomposeBitTest(select.Condition, true, false, true, Metrics) is not { } test || test.X.Type != value.Type) return null;
            var eqZero = trueValue.Value; var neZero = falseValue.Value;
            if (test.Pred == LlvmCmpPredicate.ICmpNe) (eqZero, neZero) = (neZero, eqZero);
            if (!eqZero.IsZero || neZero.IsZero || test.Mask.IsZero || !test.Mask.IsPowerOf2 || !neZero.URem(test.Mask).IsZero) return null;
            return new(test.X, neZero.UDiv(test.Mask), test.Mask, false, false);
        }
        private LlvmValue? FoldBitmaskMul(LlvmValue left, LlvmValue right)
        {
            if (MatchBitmaskMul(right) is not { } r || MatchBitmaskMul(left) is not { } l ||
                !ReferenceEquals(l.X, r.X) || !(l.Mask & r.Mask).IsZero || l.Factor != r.Factor) return null;
            var and = Builder.CreateAnd(l.X, FunnelConstant(l.X.Type, l.Mask + r.Mask));
            return Builder.CreateMul(and, FunnelConstant(and.Type, r.Factor), nuw: l.NUW && r.NUW, nsw: l.NSW && r.NSW);
        }
        private readonly record struct ZExtedSubInteger(LlvmValue Integer, LlvmAPInt Mask, ulong Offset, bool NUW, bool NSW);
        private static ZExtedSubInteger? MatchZExtedSubInteger(LlvmValue value)
        {
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } shl ||
                shl.Right is not LlvmConstantInt shlAmount || shlAmount.Value.GetActiveBits() > 64 ||
                shl.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } extension) return null;
            LlvmValue masked = extension.Operand;
            LlvmAPInt? mask = null;
            if (masked is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and && FunnelAPInt(and.Right) is { } andMask)
            { masked = and.Left; mask = andMask.Value; }
            ulong rightAmount = 0;
            LlvmValue integer = masked;
            if (masked is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt rightConstant } lshr && rightConstant.Value.GetActiveBits() <= 64)
            { integer = lshr.Left; rightAmount = rightConstant.Value.GetZExtValue(); }
            ulong leftAmount = shlAmount.Value.GetZExtValue();
            if (rightAmount > leftAmount) return null;
            var bits = mask is { } shiftedMask ? shiftedMask.Shl((uint)rightAmount) :
                LlvmAPInt.GetBitsSetFrom(integer.Type.GetScalarSizeInBits(), (uint)rightAmount);
            return new(integer, bits, leftAmount - rightAmount, shl.HasNoUnsignedWrap, shl.HasNoSignedWrap);
        }
        private LlvmValue? FoldIntegerRepackThroughZExt(LlvmValue left, LlvmValue right)
        {
            if (MatchZExtedSubInteger(left) is not { } l || MatchZExtedSubInteger(right) is not { } r ||
                !ReferenceEquals(l.Integer, r.Integer) || l.Offset != r.Offset) return null;
            var and = Builder.CreateAnd(l.Integer, FunnelConstant(l.Integer.Type, l.Mask | r.Mask), l.Integer.Name + ".mask");
            var extension = Builder.CreateCast(LlvmOpcode.ZExt, and, left.Type, l.Integer.Name + ".zext");
            var result = Builder.CreateBinary(LlvmOpcode.Shl, extension,
                FunnelConstant(left.Type, new LlvmAPInt(left.Type.GetScalarSizeInBits(), l.Offset)), nuw: l.NUW && r.NUW, nsw: l.NSW && r.NSW);
            LogicTakeName(result, left); return result;
        }
        private LlvmValue? FoldDisjointOr(LlvmValue left, LlvmValue right)
        {
            if (FoldBitmaskMul(left, right) is { } product)
            { Count("instcombine.logic.disjoint_or.bitmask_mul"); return product; }
            if (FoldIntegerRepackThroughZExt(left, right) is { } repack)
            { Count("instcombine.logic.disjoint_or.integer_repack"); return repack; }
            return null;
        }
        private LlvmValue CreateDisjointLogicOr(LlvmValue left, LlvmValue right)
        {
            var result = Builder.CreateOr(left, right);
            if (result is LlvmBinaryOperator binary) binary.IsDisjoint = true;
            return result;
        }
        private LlvmValue? ReassociateDisjointOr(LlvmValue left, LlvmValue right)
        {
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, HasOneUse: true } rhs)
            {
                if (FoldDisjointOr(left, rhs.Left) is { } a) return CreateDisjointLogicOr(a, rhs.Right);
                if (FoldDisjointOr(left, rhs.Right) is { } b) return CreateDisjointLogicOr(b, rhs.Left);
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, HasOneUse: true } lhs)
            {
                if (FoldDisjointOr(lhs.Left, right) is { } a) return CreateDisjointLogicOr(a, lhs.Right);
                if (FoldDisjointOr(lhs.Right, right) is { } b) return CreateDisjointLogicOr(b, lhs.Left);
            }
            return null;
        }
    }
}
