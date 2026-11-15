// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instruction.cpp
// isSameOperationAs / isIdenticalTo. Explicit fields adapt SubclassOptionalData.
namespace Msync.Core.Llvm;

[Flags]
public enum LlvmOperationEquivalenceFlags
{
    None = 0, CompareIgnoringAlignment = 1, CompareUsingScalarTypes = 2,
    CompareUsingIntersectedAttrs = 4, CompareCallTargets = 8
}

public abstract partial class LlvmInstruction
{
    public bool IsSameOperationAs(LlvmInstruction other, LlvmOperationEquivalenceFlags flags = LlvmOperationEquivalenceFlags.None)
    {
        bool scalarTypes = (flags & LlvmOperationEquivalenceFlags.CompareUsingScalarTypes) != 0;
        if (Opcode != other.Opcode || OperandCount != other.OperandCount ||
            !ReferenceEquals(scalarTypes ? Type.GetScalarType() : Type, scalarTypes ? other.Type.GetScalarType() : other.Type)) return false;
        for (int index = 0; index < OperandCount; ++index)
        {
            var left = GetOperand(index)!.Type;
            var right = other.GetOperand(index)!.Type;
            if (!ReferenceEquals(scalarTypes ? left.GetScalarType() : left, scalarTypes ? right.GetScalarType() : right)) return false;
        }
        if ((flags & LlvmOperationEquivalenceFlags.CompareCallTargets) != 0 && this is LlvmCallBase call &&
            !ReferenceEquals(call.CalledOperand, ((LlvmCallBase)other).CalledOperand)) return false;
        return HasSameSpecialState(other, (flags & LlvmOperationEquivalenceFlags.CompareIgnoringAlignment) != 0,
            (flags & LlvmOperationEquivalenceFlags.CompareUsingIntersectedAttrs) != 0);
    }

    public bool IsIdenticalTo(LlvmInstruction other)
    {
        if (!IsIdenticalToWhenDefined(other) || SubclassOptionalData != other.SubclassOptionalData || !SameOptionalFlags(other)) return false;
        return !IsFloatingMathOperator || ((ILlvmFastMathInstruction)this).FastMathFlags == ((ILlvmFastMathInstruction)other).FastMathFlags;
    }

    private bool SameOptionalFlags(LlvmInstruction other) => this switch
    {
        LlvmBinaryOperator left when IsOverflowingOpcode(Opcode) => left.HasNoUnsignedWrap == ((LlvmBinaryOperator)other).HasNoUnsignedWrap
            && left.HasNoSignedWrap == ((LlvmBinaryOperator)other).HasNoSignedWrap,
        LlvmBinaryOperator left when IsPossiblyExactOpcode(Opcode) => left.IsExact == ((LlvmBinaryOperator)other).IsExact,
        LlvmBinaryOperator left when Opcode == LlvmOpcode.Or => left.IsDisjoint == ((LlvmBinaryOperator)other).IsDisjoint,
        LlvmCastInstruction left when Opcode == LlvmOpcode.Trunc => left.HasNoUnsignedWrap == ((LlvmCastInstruction)other).HasNoUnsignedWrap
            && left.HasNoSignedWrap == ((LlvmCastInstruction)other).HasNoSignedWrap,
        LlvmCastInstruction left when IsPossiblyNonNegativeOpcode(Opcode) => left.IsNonNegative == ((LlvmCastInstruction)other).IsNonNegative,
        LlvmCastInstruction left when Opcode == LlvmOpcode.AddrSpaceCast => left.HasNonNull == ((LlvmCastInstruction)other).HasNonNull,
        LlvmGetElementPtrInstruction left => left.NoWrapFlags == ((LlvmGetElementPtrInstruction)other).NoWrapFlags,
        LlvmICmpInstruction left => left.SameSign == ((LlvmICmpInstruction)other).SameSign,
        _ => true
    };
}
