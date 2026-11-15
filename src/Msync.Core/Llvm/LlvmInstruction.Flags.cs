// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instruction.cpp
// dropPoisonGeneratingFlags, copyIRFlags and attribute-dropping helpers.
namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public void DropPoisonGeneratingFlags()
    {
        switch (this)
        {
            case LlvmBinaryOperator binary when IsOverflowingOpcode(Opcode):
                binary.HasNoSignedWrap = binary.HasNoUnsignedWrap = false;
                break;
            case LlvmBinaryOperator binary when IsPossiblyExactOpcode(Opcode):
                binary.IsExact = false;
                break;
            case LlvmBinaryOperator { Opcode: LlvmOpcode.Or } binary:
                binary.IsDisjoint = false;
                break;
            case LlvmGetElementPtrInstruction gep:
                gep.NoWrapFlags = LlvmGepNoWrapFlags.None;
                break;
            case LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } cast:
                cast.HasNoSignedWrap = cast.HasNoUnsignedWrap = false;
                break;
            case LlvmCastInstruction cast when IsPossiblyNonNegativeOpcode(Opcode):
                cast.IsNonNegative = false;
                break;
            case LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } cast:
                cast.HasNonNull = false;
                break;
            case LlvmICmpInstruction compare:
                compare.SameSign = false;
                break;
            case LlvmCallInstruction call when IsPoisonFlagIntrinsic(call):
                call.SetArgument(1, LlvmConstantInt.GetFalse(Context));
                break;
        }
        if (IsFloatingMathOperator && this is ILlvmFastMathInstruction floating)
            floating.FastMathFlags &= ~(LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs);
        System.Diagnostics.Debug.Assert(!HasPoisonGeneratingFlags());
    }

    private static bool IsPoisonFlagIntrinsic(LlvmCallBase call) =>
        call.CalledFunction?.IntrinsicDescriptor?.Kind.Name is "llvm.ctlz" or "llvm.cttz" or "llvm.abs";

    public void CopyIRFlags(LlvmValue value, bool includeWrapFlags = true)
    {
        if (includeWrapFlags && this is LlvmBinaryOperator binary && IsOverflowingOpcode(Opcode)
            && value is LlvmBinaryOperator source && IsOverflowingOpcode(source.Opcode))
        {
            binary.HasNoSignedWrap = source.HasNoSignedWrap;
            binary.HasNoUnsignedWrap = source.HasNoUnsignedWrap;
        }
        if (this is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc &&
            value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } sourceTrunc)
        {
            trunc.HasNoSignedWrap = sourceTrunc.HasNoSignedWrap;
            trunc.HasNoUnsignedWrap = sourceTrunc.HasNoUnsignedWrap;
        }
        if (this is LlvmBinaryOperator exact && IsPossiblyExactOpcode(Opcode) &&
            value is LlvmBinaryOperator sourceExact && IsPossiblyExactOpcode(sourceExact.Opcode))
            exact.IsExact = sourceExact.IsExact;
        if (this is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } disjoint &&
            value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } sourceDisjoint)
            disjoint.IsDisjoint = sourceDisjoint.IsDisjoint;
        if (IsFloatingMathOperator && this is ILlvmFastMathInstruction floating &&
            value is LlvmInstruction { IsFloatingMathOperator: true } sourceFp && sourceFp is ILlvmFastMathInstruction flags)
            floating.FastMathFlags = flags.FastMathFlags;
        if (this is LlvmGetElementPtrInstruction gep && value is LlvmGetElementPtrInstruction sourceGep)
            gep.NoWrapFlags |= sourceGep.NoWrapFlags;
        if (this is LlvmCastInstruction cast && IsPossiblyNonNegativeOpcode(Opcode) &&
            value is LlvmCastInstruction sourceCast && IsPossiblyNonNegativeOpcode(sourceCast.Opcode))
            cast.IsNonNegative = sourceCast.IsNonNegative;
        if (this is LlvmICmpInstruction compare && value is LlvmICmpInstruction sourceCompare)
            compare.SameSign = sourceCompare.SameSign;
        if (this is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } address &&
            value is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } sourceAddress)
        {
            System.Diagnostics.Debug.Assert(((LlvmPointerType)address.Operand.Type).AddressSpace ==
                ((LlvmPointerType)sourceAddress.Operand.Type).AddressSpace);
            address.HasNonNull = sourceAddress.HasNonNull;
        }
    }

    public bool HasPoisonGeneratingAttributes()
    {
        if (this is not LlvmCallBase call) return false;
        if (call.ReturnAttributes.HasPoisonGeneratingAttributes) return true;
        for (int index = 0; index < call.ArgumentCount; ++index)
            if (call.GetParamAttributes(index).HasPoisonGeneratingAttributes) return true;
        return false;
    }

    public bool HasPoisonGeneratingAnnotations() => HasPoisonGeneratingFlags() || HasPoisonGeneratingAttributes();

    public void DropPoisonGeneratingAttributes()
    {
        if (this is not LlvmCallBase call) return;
        call.ReturnAttributes = call.ReturnAttributes.WithoutPoisonGeneratingAttributes();
        for (int index = 0; index < call.ArgumentCount; ++index)
            call.SetParamAttributes(index, call.GetOwnParamAttributes(index).WithoutPoisonGeneratingAttributes());
    }

    public void DropPoisonGeneratingAnnotations()
    {
        DropPoisonGeneratingFlags();
        DropPoisonGeneratingAttributes();
    }

    public void DropUBImplyingAttrsAndMetadata()
    {
        if (this is not LlvmCallBase call) return;
        call.ReturnAttributes = call.ReturnAttributes.WithoutUBImplyingAttributes();
        for (int index = 0; index < call.ArgumentCount; ++index)
            call.SetParamAttributes(index, call.GetOwnParamAttributes(index).WithoutUBImplyingAttributes());
    }

    public bool HasUBImplyingAttrs()
    {
        if (this is not LlvmCallBase call) return false;
        if (call.ReturnAttributes.HasUBImplyingAttributes) return true;
        for (int index = 0; index < call.ArgumentCount; ++index)
            if (call.IsPassingUndefUB(index)) return true;
        return false;
    }
}
