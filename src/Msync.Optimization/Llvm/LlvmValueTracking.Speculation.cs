// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// isSafeToSpeculativelyExecute[WithOpcode], mayHaveNonDefUseDependency.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsSafeToSpeculativelyExecute(LlvmInstruction instruction, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, bool useVariableInfo = true, bool ignoreUbImplyingAttrs = true,
        Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null) => IsSafeToSpeculativelyExecuteWithOpcode(instruction.Opcode,
            instruction, context, tree, useVariableInfo, ignoreUbImplyingAttrs, metrics, assumptions, allowEphemerals, conditions);

    public static bool IsSafeToSpeculativelyExecuteWithVariableReplaced(LlvmInstruction instruction,
        bool ignoreUbImplyingAttrs = true, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null) =>
        IsSafeToSpeculativelyExecute(instruction, useVariableInfo: false, ignoreUbImplyingAttrs: ignoreUbImplyingAttrs, metrics: metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions);

    public static bool IsSafeToSpeculativelyExecuteWithOpcode(LlvmOpcode opcode, LlvmInstruction instruction,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, bool useVariableInfo = true,
        bool ignoreUbImplyingAttrs = true, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        if (opcode != instruction.Opcode)
        {
            Debug.Assert(opcode is < LlvmOpcode.Add or > LlvmOpcode.Xor || HasLeadingOperandTypes(instruction, 2));
            Debug.Assert(opcode != LlvmOpcode.FNeg || HasLeadingOperandTypes(instruction, 1));
        }
        Count(metrics, "value_tracking.speculation.queries");
        bool result = CanSpeculateOpcode(opcode, instruction, context, tree, useVariableInfo, ignoreUbImplyingAttrs, metrics, assumptions, allowEphemerals, conditions);
        Count(metrics, result ? "value_tracking.speculation.movable" : "value_tracking.speculation.not_movable");
        return result;
    }

    private static bool HasLeadingOperandTypes(LlvmInstruction instruction, int count)
    {
        if (instruction.OperandCount < count) return false;
        for (int n = 0; n < count; n++)
            if (!ReferenceEquals(instruction.GetOperand(n)!.Type, instruction.Type)) return false;
        return true;
    }

    private static bool CanSpeculateOpcode(LlvmOpcode opcode, LlvmInstruction instruction,
        LlvmInstruction? context, LlvmDominatorTree? tree, bool useVariableInfo, bool ignoreUbImplyingAttrs,
        Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null)
    {
        switch (opcode)
        {
            case LlvmOpcode.UDiv:
            case LlvmOpcode.URem:
                Count(metrics, "value_tracking.speculation.unsigned_divrem");
                if (instruction.GetOperand(1) is not LlvmConstantInt unsignedDivisor)
                { Count(metrics, "value_tracking.speculation.divisor_nonconstant"); return false; }
                if (unsignedDivisor.IsZero) Count(metrics, "value_tracking.speculation.divisor_zero");
                return !unsignedDivisor.IsZero;
            case LlvmOpcode.SDiv:
            case LlvmOpcode.SRem:
                Count(metrics, "value_tracking.speculation.signed_divrem");
                if (instruction.GetOperand(1) is not LlvmConstantInt denominator)
                { Count(metrics, "value_tracking.speculation.divisor_nonconstant"); return false; }
                if (denominator.IsZero) { Count(metrics, "value_tracking.speculation.divisor_zero"); return false; }
                if (!denominator.IsMinusOne) return true;
                if (instruction.GetOperand(0) is not LlvmConstantInt numerator)
                { Count(metrics, "value_tracking.speculation.signed_numerator_nonconstant"); return false; }
                if (numerator.Value.IsMinSignedValue) Count(metrics, "value_tracking.speculation.signed_divrem_overflow");
                return !numerator.Value.IsMinSignedValue;
            case LlvmOpcode.Load:
                if (!useVariableInfo) { Count(metrics, "value_tracking.speculation.load_variable_info_disabled"); return false; }
                if (instruction is not LlvmLoadInstruction load) return false;
                if (!load.IsUnordered)
                { Count(metrics, "value_tracking.speculation.load_ordering"); return false; }
                // Sanitizer function attributes are absent; source suppression has no additional facts.
                Count(metrics, "value_tracking.speculation.load");
                return IsDereferenceableAndAlignedPointer(load.PointerOperand, load.Type, load.Alignment,
                    load.Parent!.Module!.DataLayout, context, tree, metrics: metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions);
            case LlvmOpcode.Call:
                bool speculatable = instruction is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Speculatable: true };
                // Represented min/max declarations carry Speculatable; ordinary calls do not.
                // No UB-implying call attributes are currently represented.
                Count(metrics, speculatable ? "value_tracking.speculation.call_speculatable_intrinsic" : "value_tracking.speculation.call_no_speculatable_attribute");
                return speculatable;
            case LlvmOpcode.HeapAlloc:
            case LlvmOpcode.FileOut:
                Count(metrics, "value_tracking.speculation.project_effect");
                return false;
            case LlvmOpcode.FileIn:
                bool constructor = instruction is LlvmFileInInst { Format: LlvmFileInFormat.Lines or LlvmFileInFormat.Bytes };
                Count(metrics, constructor ? "value_tracking.speculation.project_constructor_identity" : "value_tracking.speculation.project_file_read");
                return false;
            case LlvmOpcode.VAArg:
            case LlvmOpcode.Alloca:
            case LlvmOpcode.Invoke:
            case LlvmOpcode.CallBr:
            case LlvmOpcode.PHI:
            case LlvmOpcode.Store:
            case LlvmOpcode.Ret:
            case LlvmOpcode.UncondBr:
            case LlvmOpcode.CondBr:
            case LlvmOpcode.IndirectBr:
            case LlvmOpcode.Switch:
            case LlvmOpcode.Unreachable:
            case LlvmOpcode.Fence:
            case LlvmOpcode.AtomicRMW:
            case LlvmOpcode.AtomicCmpXchg:
            case LlvmOpcode.LandingPad:
            case LlvmOpcode.Resume:
            case LlvmOpcode.CatchSwitch:
            case LlvmOpcode.CatchPad:
            case LlvmOpcode.CatchRet:
            case LlvmOpcode.CleanupPad:
            case LlvmOpcode.CleanupRet:
                Count(metrics, "value_tracking.speculation.effects_opcode");
                return false;
            default:
                // LLVM permits poison-producing arithmetic, flags and out-of-range shifts here.
                Count(metrics, "value_tracking.speculation.default_opcode");
                return true;
        }
    }

    public static bool MayHaveNonDefUseDependency(LlvmInstruction instruction, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        if (instruction.MayReadFromMemory() || instruction.MayWriteToMemory())
        { Count(metrics, "value_tracking.non_def_use.memory"); return true; }
        if (!IsSafeToSpeculativelyExecute(instruction, metrics: metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions))
        { Count(metrics, "value_tracking.non_def_use.speculation"); return true; }
        if (!IsGuaranteedToTransferExecutionToSuccessor(instruction, metrics))
        { Count(metrics, "value_tracking.non_def_use.transfer"); return true; }
        return false;
    }
}
