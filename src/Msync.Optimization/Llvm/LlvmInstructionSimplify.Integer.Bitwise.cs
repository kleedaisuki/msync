// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyAndInst/simplifyOrInst/simplifyXorInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerBitwise(LlvmOpcode opcode, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.bitwise.queries");
        if (FoldOrCommuteConstant(opcode, ref lhs, ref rhs, q) is { } folded) return folded;
        if (rhs is LlvmPoisonValue) return rhs;
        return opcode switch
        {
            LlvmOpcode.And => SimplifyBitwiseAnd(lhs, rhs, q, depth),
            LlvmOpcode.Or => SimplifyBitwiseOr(lhs, rhs, q, depth),
            LlvmOpcode.Xor => SimplifyBitwiseXor(lhs, rhs, q, depth),
            _ => throw new InvalidOperationException("Expected integer bitwise opcode.")
        };
    }

    private static LlvmValue? SimplifyBitwiseAnd(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (q.IsUndef(rhs)) return IntegerZero(lhs);
        if (lhs == rhs) return lhs;
        if (IsIntegerZero(rhs)) return IntegerZero(lhs);
        if (IsIntegerAllOnes(rhs)) return lhs;
        if (SimplifyAndCommutative(lhs, rhs, q, depth) is { } a) return a;
        if (SimplifyAndCommutative(rhs, lhs, q, depth) is { } b) return b;
        if (SimplifyLogicOfAddSub(lhs, rhs, LlvmOpcode.And) is { } addsub) return addsub;
        if (rhs is LlvmConstantInt mask)
        {
            if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt shift } &&
                (~mask.Value).LShr(shift.Value).IsZero) return lhs;
            if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt rightShift } &&
                (~mask.Value).Shl(rightShift.Value).IsZero) return lhs;
        }
        if (rhs is LlvmConstantInt power && power.Value.IsPowerOf2 &&
            lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } minusOne && IsIntegerAllOnes(minusOne.Right) &&
            BitwisePowerOfTwo(minusOne.Left, false, q))
        {
            LlvmKnownBits known = BitwiseKnownBits(minusOne.Left, q);
            if (power.Value.GetActiveBits() >= known.GetMaxValue().GetActiveBits()) return IntegerZero(rhs);
        }
        if (SimplifyBitwiseComparisons(lhs, rhs, true, q) is { } comparison) return comparison;
        if (BitwiseZExtSExt(lhs, rhs, false) is { } extension) return extension;
        if (ReassociateIntegerBin(LlvmOpcode.And, lhs, rhs, q, depth) is { } reassociated) return reassociated;
        if (ExpandCommutativeIntegerBin(LlvmOpcode.And, lhs, rhs, LlvmOpcode.Or, q, depth) is { } orExpansion) return orExpansion;
        if (ExpandCommutativeIntegerBin(LlvmOpcode.And, lhs, rhs, LlvmOpcode.Xor, q, depth) is { } xorExpansion) return xorExpansion;
        if (lhs is LlvmSelectInstruction || rhs is LlvmSelectInstruction)
        {
            if (lhs.Type.IsIntegerTy(1))
            {
                if (rhs is LlvmSelectInstruction r && r.Condition == lhs && IsIntegerZero(r.FalseValue)) return rhs;
                if (lhs is LlvmSelectInstruction l && l.Condition == rhs && IsIntegerZero(l.FalseValue)) return lhs;
            }
            if (ThreadIntegerSelect(LlvmOpcode.And, lhs, rhs, q, depth) is { } select) return select;
        }
        if ((lhs is LlvmPhiInstruction || rhs is LlvmPhiInstruction) &&
            ThreadIntegerPhi(LlvmOpcode.And, lhs, rhs, q, depth) is { } phi) return phi;
        if (q.UseInstructionInfo && rhs is LlvmConstantInt extractMask &&
            SimplifyAndShiftedOr(lhs, extractMask.Value, q) is { } shiftedOr) return shiftedOr;
        if (BitwiseSharedOrXors(lhs, rhs)) return IntegerZero(lhs);
        if (BitwiseComplementaryXors(lhs, rhs)) return IntegerZero(lhs);
        if (lhs.Type.IsIntegerTy(1) && SimplifyBitwiseImplied(lhs, rhs, true, q) is { } implied) return implied;
        return SimplifyIntegerByDomEq(LlvmOpcode.And, lhs, rhs, q, depth);
    }

    private static LlvmValue? SimplifyBitwiseOr(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (q.IsUndef(rhs) || IsIntegerAllOnes(rhs)) return IntegerAllOnes(lhs);
        if (lhs == rhs || IsIntegerZero(rhs)) return lhs;
        if (SimplifyOrLogic(lhs, rhs) is { } a) return a;
        if (SimplifyOrLogic(rhs, lhs) is { } b) return b;
        if (SimplifyLogicOfAddSub(lhs, rhs, LlvmOpcode.Or) is { } addsub) return addsub;
        if (BitwiseRotatedAllOnes(lhs, rhs)) return IntegerAllOnes(lhs);
        if (BitwiseZExtSExt(lhs, rhs, true) is { } extension) return extension;
        // Intrinsic fshl/fshr/overflow patterns require intrinsic identity, not a native function name.
        if (SimplifyAndOrWithICmpEq(LlvmOpcode.Or, lhs, rhs, q, depth) is { } eq0) return eq0;
        if (SimplifyAndOrWithICmpEq(LlvmOpcode.Or, rhs, lhs, q, depth) is { } eq1) return eq1;
        if (SimplifyBitwiseComparisons(lhs, rhs, false, q) is { } comparison) return comparison;
        if (ReassociateIntegerBin(LlvmOpcode.Or, lhs, rhs, q, depth) is { } reassociated) return reassociated;
        if (ExpandCommutativeIntegerBin(LlvmOpcode.Or, lhs, rhs, LlvmOpcode.And, q, depth) is { } expansion) return expansion;
        if (lhs is LlvmSelectInstruction || rhs is LlvmSelectInstruction)
        {
            if (lhs.Type.IsIntegerTy(1))
            {
                if (rhs is LlvmSelectInstruction r && r.Condition == lhs && IsIntegerOne(r.TrueValue)) return rhs;
                if (lhs is LlvmSelectInstruction l && l.Condition == rhs && IsIntegerOne(l.TrueValue)) return lhs;
            }
            if (ThreadIntegerSelect(LlvmOpcode.Or, lhs, rhs, q, depth) is { } select) return select;
        }
        if (SimplifyOrMaskedAdd(lhs, rhs, q) is { } maskedAdd) return maskedAdd;
        if ((lhs is LlvmPhiInstruction || rhs is LlvmPhiInstruction) &&
            ThreadIntegerPhi(LlvmOpcode.Or, lhs, rhs, q, depth) is { } phi) return phi;
        if (BitwiseComplementaryXors(lhs, rhs)) return IntegerAllOnes(lhs);
        if (lhs.Type.IsIntegerTy(1) && SimplifyBitwiseImplied(lhs, rhs, false, q) is { } implied) return implied;
        return SimplifyIntegerByDomEq(LlvmOpcode.Or, lhs, rhs, q, depth);
    }

    private static LlvmValue? SimplifyBitwiseXor(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (q.IsUndef(rhs)) return rhs;
        if (IsIntegerZero(rhs)) return lhs;
        if (lhs == rhs) return IntegerZero(lhs);
        if (IsIntegerNot(lhs, rhs) || IsIntegerNot(rhs, lhs)) return IntegerAllOnes(lhs);
        if (SimplifyXorAndOrNot(lhs, rhs) is { } a) return a;
        if (SimplifyXorAndOrNot(rhs, lhs) is { } b) return b;
        if (SimplifyLogicOfAddSub(lhs, rhs, LlvmOpcode.Xor) is { } addsub) return addsub;
        if (ReassociateIntegerBin(LlvmOpcode.Xor, lhs, rhs, q, depth) is { } reassociated) return reassociated;
        if (SimplifyIntegerByDomEq(LlvmOpcode.Xor, lhs, rhs, q, depth) is { } dom) return dom;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true } subtraction &&
            subtraction.Left == rhs && rhs is LlvmConstantInt mask && BitwiseIsLowMask(mask.Value)) return subtraction.Right;
        return null;
    }
}
