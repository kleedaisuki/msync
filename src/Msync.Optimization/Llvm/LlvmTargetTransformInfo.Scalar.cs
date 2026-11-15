// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/TargetTransformInfoImpl.h generic base costs and CRTP instruction dispatch.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTargetTransformInfo
{
    public LlvmInstructionCost GetInstructionCost(LlvmUser user, LlvmTargetCostKind kind) =>
        GetInstructionCost(user, user.Operands.Select(value => value!).ToArray(), kind);

    public LlvmInstructionCost GetInstructionCost(LlvmUser user, IReadOnlyList<LlvmValue> operands,
        LlvmTargetCostKind kind)
    {
        Count("tti.instruction.queries");
        if (user is LlvmCallBase call)
        {
            if (call.CalledFunction?.IntrinsicDescriptor is { } intrinsic)
                return GetIntrinsicInstrCost(intrinsic.Kind, call.Type, operands, kind);
            Count("tti.instruction.call");
            return call.CalledFunction is { } callee
                ? IsLoweredToCall(callee) ? new(callee.ArgumentCount + 1) : LlvmInstructionCost.Basic
                : new(call.ArgumentCount + 1);
        }
        if (user is not LlvmInstruction instruction) return LlvmInstructionCost.Basic;
        switch (instruction.Opcode)
        {
            case LlvmOpcode.UncondBr:
            case LlvmOpcode.CondBr:
            case LlvmOpcode.Ret:
            case LlvmOpcode.PHI:
            case LlvmOpcode.Switch:
                return GetCFInstrCost(instruction.Opcode, kind, instruction);
            case LlvmOpcode.Freeze:
                Count("tti.instruction.freeze");
                return LlvmInstructionCost.Free;
            case LlvmOpcode.ExtractValue:
            case LlvmOpcode.InsertValue:
                return GetInsertExtractValueCost(instruction.Opcode, kind);
            case LlvmOpcode.Alloca:
                Count("tti.instruction.alloca");
                return ((LlvmAllocaInstruction)instruction).IsStaticAlloca
                    ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic;
            case LlvmOpcode.GetElementPtr:
                var gep = (LlvmGetElementPtrInstruction)instruction;
                LlvmType? access = instruction.HasOneUser ? instruction.Users.First() switch
                {
                    LlvmLoadInstruction load => load.Type,
                    LlvmStoreInstruction store => store.ValueOperand.Type,
                    _ => null
                } : null;
                return GetGepCost(gep.SourceElementType, operands[0], operands.Skip(1).ToArray(), kind, access);
            case >= LlvmOpcode.Add and <= LlvmOpcode.Xor:
            case LlvmOpcode.FNeg:
                return GetArithmeticInstrCost(instruction.Opcode, instruction.Type, kind, operands, instruction);
            case >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast:
                return GetCastInstrCost(instruction.Opcode, instruction.Type, operands[0].Type, kind, instruction);
            case LlvmOpcode.Store:
                var storeInstruction = (LlvmStoreInstruction)instruction;
                return GetMemoryOpCost(instruction.Opcode, operands[0].Type, storeInstruction.Alignment,
                    ((LlvmPointerType)storeInstruction.PointerOperand.Type).AddressSpace, kind, instruction);
            case LlvmOpcode.Load:
                var loadInstruction = (LlvmLoadInstruction)instruction;
                LlvmType loadType = instruction.Type;
                if (kind == LlvmTargetCostKind.CodeSize && instruction.HasOneUse && !loadType.IsVectorTy()
                    && instruction.Users.First() is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation)
                    loadType = truncation.Type;
                return GetMemoryOpCost(instruction.Opcode, loadType, loadInstruction.Alignment,
                    ((LlvmPointerType)loadInstruction.PointerOperand.Type).AddressSpace, kind, instruction);
            case LlvmOpcode.Select:
                if (instruction.Type.GetScalarType().IsIntegerTy(1))
                {
                    if (operands[2] is LlvmConstant { IsNullValue: true })
                        return GetArithmeticInstrCost(LlvmOpcode.And, instruction.Type, kind,
                            [operands[0], operands[1]], instruction);
                    if (operands[1] is LlvmConstantInt { Value.IsAllOnes: true })
                        return GetArithmeticInstrCost(LlvmOpcode.Or, instruction.Type, kind,
                            [operands[0], operands[2]], instruction);
                }
                return GetCmpSelInstrCost(instruction.Opcode, instruction.Type, operands[0].Type, kind, instruction);
            case LlvmOpcode.ICmp:
            case LlvmOpcode.FCmp:
                return GetCmpSelInstrCost(instruction.Opcode, operands[0].Type, instruction.Type, kind, instruction);
            default:
                Count("tti.instruction.basic_default");
                return LlvmInstructionCost.Basic;
        }
    }

    public LlvmInstructionCost GetArithmeticInstrCost(LlvmOpcode opcode, LlvmType type,
        LlvmTargetCostKind kind, IReadOnlyList<LlvmValue>? arguments = null, LlvmInstruction? instruction = null)
    {
        Count("tti.arithmetic.queries");
        if (opcode is LlvmOpcode.FDiv or LlvmOpcode.FRem or LlvmOpcode.SDiv or LlvmOpcode.SRem
            or LlvmOpcode.UDiv or LlvmOpcode.URem)
        {
            Count("tti.arithmetic.expensive");
            return LlvmInstructionCost.Expensive;
        }
        // The represented intrinsic registry has no widenable-condition intrinsic.
        if (kind == LlvmTargetCostKind.Latency && type.GetScalarType().IsFloatingPointTy())
        {
            Count("tti.arithmetic.fp_latency");
            return new(3);
        }
        return LlvmInstructionCost.Basic;
    }

    public LlvmInstructionCost GetCastInstrCost(LlvmOpcode opcode, LlvmType destination, LlvmType source,
        LlvmTargetCostKind kind, LlvmInstruction? instruction = null)
    {
        Count("tti.cast.queries");
        switch (opcode)
        {
            case LlvmOpcode.IntToPtr:
                if (layout.IsLegalInteger(source.GetScalarSizeInBits())
                    && source.GetScalarSizeInBits() <= layout.GetPointerTypeSizeInBits(destination))
                    return LlvmInstructionCost.Free;
                break;
            case LlvmOpcode.PtrToAddr:
                Debug.Assert(destination.GetScalarSizeInBits() ==
                    layout.GetAddressSizeInBits(((LlvmPointerType)source.GetScalarType()).AddressSpace));
                if (layout.IsLegalInteger(destination.GetScalarSizeInBits())) return LlvmInstructionCost.Free;
                break;
            case LlvmOpcode.PtrToInt:
                if (layout.IsLegalInteger(destination.GetScalarSizeInBits())
                    && destination.GetScalarSizeInBits() >= layout.GetPointerTypeSizeInBits(source))
                    return LlvmInstructionCost.Free;
                break;
            case LlvmOpcode.BitCast:
                if (destination == source || destination.IsPointerTy() && source.IsPointerTy())
                    return LlvmInstructionCost.Free;
                break;
            case LlvmOpcode.Trunc:
                var size = layout.GetTypeSizeInBits(destination);
                if (!size.IsScalable && layout.IsLegalInteger(size.GetFixedValue())) return LlvmInstructionCost.Free;
                break;
        }
        return LlvmInstructionCost.Basic;
    }

    public LlvmInstructionCost GetCFInstrCost(LlvmOpcode opcode, LlvmTargetCostKind kind,
        LlvmInstruction? instruction = null)
    {
        Count("tti.control.queries");
        return opcode == LlvmOpcode.PHI && kind != LlvmTargetCostKind.ReciprocalThroughput
            ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic;
    }

    public LlvmInstructionCost GetCmpSelInstrCost(LlvmOpcode opcode, LlvmType valueType,
        LlvmType conditionType, LlvmTargetCostKind kind, LlvmInstruction? instruction = null)
    {
        Count("tti.compare_select.queries");
        return LlvmInstructionCost.Basic;
    }

    public LlvmInstructionCost GetInsertExtractValueCost(LlvmOpcode opcode, LlvmTargetCostKind kind)
    {
        Count("tti.aggregate.queries");
        return opcode == LlvmOpcode.InsertValue && kind != LlvmTargetCostKind.ReciprocalThroughput
            ? LlvmInstructionCost.Basic : LlvmInstructionCost.Free;
    }

    public LlvmInstructionCost GetMemoryOpCost(LlvmOpcode opcode, LlvmType type, LlvmAlign alignment,
        uint addressSpace, LlvmTargetCostKind kind, LlvmInstruction? instruction = null)
    {
        Count("tti.memory.queries");
        return LlvmInstructionCost.Basic;
    }

    public LlvmInstructionCost GetIntrinsicInstrCost(LlvmIntrinsicKind intrinsic, LlvmType returnType,
        IReadOnlyList<LlvmValue> arguments, LlvmTargetCostKind kind)
    {
        Count("tti.intrinsic.queries");
        var descriptor = LlvmIntrinsic.GetDescriptor(intrinsic);
        return descriptor.IsAssume || descriptor.IsLifetimeStartOrEnd || descriptor.IsInvariantStartOrEnd ||
            descriptor.IsLaunderInvariantGroup ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic;
    }

    public bool IsExpensiveToSpeculativelyExecute(LlvmInstruction instruction) =>
        GetInstructionCost(instruction, LlvmTargetCostKind.SizeAndLatency) >= LlvmInstructionCost.Expensive;

    public bool IsLoweredToCall(LlvmFunction function)
    {
        Count("tti.call_lowering.queries");
        if (function.IntrinsicDescriptor is not null) return false;
        if (function.HasLocalLinkage || !function.HasName) return true;
        return function.Name is not ("copysign" or "copysignf" or "copysignl"
            or "fabs" or "fabsf" or "fabsl" or "fmin" or "fminf" or "fminl" or "fmax" or "fmaxf" or "fmaxl"
            or "sin" or "sinf" or "sinl" or "cos" or "cosf" or "cosl" or "tan" or "tanf" or "tanl"
            or "asin" or "asinf" or "asinl" or "acos" or "acosf" or "acosl" or "atan" or "atanf" or "atanl"
            or "atan2" or "atan2f" or "atan2l" or "sinh" or "sinhf" or "sinhl" or "cosh" or "coshf" or "coshl"
            or "tanh" or "tanhf" or "tanhl" or "sqrt" or "sqrtf" or "sqrtl" or "exp10" or "exp10l" or "exp10f"
            or "pow" or "powf" or "powl" or "exp2" or "exp2l" or "exp2f" or "floor" or "floorf"
            or "ceil" or "round" or "ffs" or "ffsl" or "abs" or "labs" or "llabs");
    }

    public bool HasBranchDivergence(LlvmFunction? function = null) => false;
    public bool HasConditionalLoadStoreForType(LlvmType type, bool isStore) => false;
    public LlvmInstructionCost GetBranchMispredictPenalty() => LlvmInstructionCost.Free;
    public LlvmBranchProbability GetPredictableBranchThreshold() => new(99, 100);
    public bool IsTypeLegal(LlvmType type) => false;
    public bool ShouldBuildLookupTables() => true;
    public bool ShouldBuildLookupTablesForConstant(LlvmConstant constant) => true;
    public uint GetMinimumLookupTableEntryBitWidth() => 8;
    public bool ShouldBuildRelLookupTables() => false;
    public bool IsLegalAddImmediate(long immediate) => false;
    public bool IsLegalICmpImmediate(long immediate) => false;
    public bool IsValidAddrSpaceCast(uint fromAddressSpace, uint toAddressSpace) => false;
}
