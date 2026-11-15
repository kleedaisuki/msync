// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: lib/IR/Instruction.cpp
// Instruction::andIRFlags; lib/IR/Operator.cpp Operator::hasPoisonGeneratingFlags;
// include/llvm/IR/Operator.h FPMathOperator::classof.
namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public bool IsFloatingMathOperator => Opcode switch
    {
        LlvmOpcode.FNeg or LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or
            LlvmOpcode.FRem or LlvmOpcode.FPTrunc or LlvmOpcode.FPExt or LlvmOpcode.UIToFP or
            LlvmOpcode.SIToFP or LlvmOpcode.FCmp => true,
        LlvmOpcode.PHI or LlvmOpcode.Select or LlvmOpcode.Call => IsSupportedFloatingMathType(Type),
        _ => false
    };

    private static bool IsSupportedFloatingMathType(LlvmType type)
    {
        if (type.IsFPOrFPVectorTy()) return true;
        if (type is LlvmStructType structure)
        {
            if (!structure.IsLiteral || !structure.ContainsHomogeneousTypes()) return false;
            type = structure.Elements[0];
        }
        else if (type is LlvmArrayType)
        {
            do { type = ((LlvmArrayType)type).ElementType; } while (type is LlvmArrayType);
        }
        return type.IsFPOrFPVectorTy();
    }

    public bool HasPoisonGeneratingFlags() => Opcode switch
    {
        LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl =>
            this is LlvmBinaryOperator { HasNoUnsignedWrap: true } or LlvmBinaryOperator { HasNoSignedWrap: true },
        LlvmOpcode.Trunc => this is LlvmCastInstruction { HasNoUnsignedWrap: true } or LlvmCastInstruction { HasNoSignedWrap: true },
        LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.AShr or LlvmOpcode.LShr => ((LlvmBinaryOperator)this).IsExact,
        LlvmOpcode.Or => ((LlvmBinaryOperator)this).IsDisjoint,
        LlvmOpcode.GetElementPtr => ((LlvmGetElementPtrInstruction)this).NoWrapFlags != LlvmGepNoWrapFlags.None,
        LlvmOpcode.UIToFP or LlvmOpcode.ZExt => ((LlvmCastInstruction)this).IsNonNegative,
        LlvmOpcode.ICmp => ((LlvmICmpInstruction)this).SameSign,
        LlvmOpcode.AddrSpaceCast => ((LlvmCastInstruction)this).HasNonNull,
        LlvmOpcode.Call when this is LlvmCallBase call && IsPoisonFlagIntrinsic(call) &&
            call.GetArgument(1) is LlvmConstantInt { IsZero: false } => true,
        _ => IsFloatingMathOperator && this is ILlvmFastMathInstruction floating &&
            (floating.FastMathFlags & (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs)) != 0
    };

    public void AndIRFlags(LlvmValue value)
    {
        if (this is LlvmBinaryOperator destination && value is LlvmBinaryOperator source)
            IntersectBinaryFlags(destination, source);
        if (this is LlvmCastInstruction targetCast && value is LlvmCastInstruction sourceCast)
            IntersectCastFlags(targetCast, sourceCast);
        if (IsFloatingMathOperator && this is ILlvmFastMathInstruction targetFp &&
            value is LlvmInstruction { IsFloatingMathOperator: true } sourceInstruction && sourceInstruction is ILlvmFastMathInstruction sourceFp)
            targetFp.FastMathFlags &= sourceFp.FastMathFlags;
        if (this is LlvmGetElementPtrInstruction targetGep && value is LlvmGetElementPtrInstruction sourceGep)
            targetGep.NoWrapFlags &= sourceGep.NoWrapFlags;
        if (this is LlvmICmpInstruction targetCompare && value is LlvmICmpInstruction sourceCompare)
            targetCompare.SameSign &= sourceCompare.SameSign;
    }

    private static void IntersectBinaryFlags(LlvmBinaryOperator destination, LlvmBinaryOperator source)
    {
        if (IsOverflowingOpcode(destination.Opcode) && IsOverflowingOpcode(source.Opcode))
        {
            destination.HasNoSignedWrap &= source.HasNoSignedWrap;
            destination.HasNoUnsignedWrap &= source.HasNoUnsignedWrap;
        }
        if (IsPossiblyExactOpcode(destination.Opcode) && IsPossiblyExactOpcode(source.Opcode)) destination.IsExact &= source.IsExact;
        if (destination.Opcode == LlvmOpcode.Or && source.Opcode == LlvmOpcode.Or) destination.IsDisjoint &= source.IsDisjoint;
    }

    private static void IntersectCastFlags(LlvmCastInstruction destination, LlvmCastInstruction source)
    {
        if (destination.Opcode == LlvmOpcode.Trunc && source.Opcode == LlvmOpcode.Trunc)
        {
            destination.HasNoSignedWrap &= source.HasNoSignedWrap;
            destination.HasNoUnsignedWrap &= source.HasNoUnsignedWrap;
        }
        if (IsPossiblyNonNegativeOpcode(destination.Opcode) && IsPossiblyNonNegativeOpcode(source.Opcode))
            destination.IsNonNegative &= source.IsNonNegative;
        if (destination.Opcode == LlvmOpcode.AddrSpaceCast && source.Opcode == LlvmOpcode.AddrSpaceCast)
        {
            System.Diagnostics.Debug.Assert(((LlvmPointerType)destination.Operand.Type).AddressSpace == ((LlvmPointerType)source.Operand.Type).AddressSpace);
            destination.HasNonNull &= source.HasNonNull;
        }
    }

    private static bool IsOverflowingOpcode(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl;
    private static bool IsPossiblyExactOpcode(LlvmOpcode opcode) => opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.AShr or LlvmOpcode.LShr;
    private static bool IsPossiblyNonNegativeOpcode(LlvmOpcode opcode) => opcode is LlvmOpcode.ZExt or LlvmOpcode.UIToFP;
}
