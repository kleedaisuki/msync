// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp,
// isKnownToBeAPowerOfTwo, isPowerOfTwoRecurrence, matchTwoInputRecurrence.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static bool IsKnownToBeAPowerOfTwo(LlvmValue value, bool orZero = false, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        bool result = IsKnownToBeAPowerOfTwoImpl(value, orZero, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        Count(metrics, result ? "value_facts.power.proven" : "value_facts.power.unknown");
        return result;
    }

    private static bool IsKnownToBeAPowerOfTwoImpl(LlvmValue value, bool orZero, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_facts.power.visits");
        if (value is LlvmConstantInt constant) return constant.Value.IsPowerOf2 || (orZero && constant.IsZero);
        if (value is LlvmConstant) return false;
        if (orZero && value.Type.IsIntegerTy(1)) return true;
        if (IsPowerOfTwoFromAssume(value, orZero, context, tree, metrics, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsPowerOfTwoFromDomConditions(value, orZero, context, tree, metrics, conditions, condContext)) return true;
        if (value is not LlvmInstruction instruction) return false;
        if (instruction is LlvmBinaryOperator shift &&
            ((shift.Opcode == LlvmOpcode.Shl && shift.Left is LlvmConstantInt { IsOne: true }) ||
             (shift.Opcode == LlvmOpcode.LShr && shift.Left is LlvmConstantInt { Value.IsMinSignedValue: true }))) return true;
        if (depth++ == MaxDepth) { Count(metrics, "value_facts.power.depth_limit"); return false; }
        bool Power(LlvmValue operand, bool? zero = null) => IsKnownToBeAPowerOfTwo(operand, zero ?? orZero, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        switch (instruction)
        {
            case LlvmCastInstruction cast when cast.Opcode == LlvmOpcode.ZExt:
                return Power(cast.Operand);
            case LlvmCastInstruction cast when cast.Opcode == LlvmOpcode.Trunc:
                return orZero && Power(cast.Operand);
            case LlvmBinaryOperator binary:
                Count(metrics, "value_facts.power.binary");
                switch (binary.Opcode)
                {
                    case LlvmOpcode.Shl:
                        return (orZero || (useInstructionInfo && binary.HasNoUnsignedWrap) || (useInstructionInfo && binary.HasNoSignedWrap)) && Power(binary.Left);
                    case LlvmOpcode.LShr:
                        return (orZero || (useInstructionInfo && binary.IsExact)) && Power(binary.Left);
                    case LlvmOpcode.UDiv:
                        return (useInstructionInfo && binary.IsExact) && Power(binary.Left);
                    case LlvmOpcode.Mul:
                        return Power(binary.Right) && Power(binary.Left) &&
                            (orZero || IsKnownNonZero(binary, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext));
                    case LlvmOpcode.And:
                        if (orZero && (Power(binary.Right, true) || Power(binary.Left, true))) return true;
                        if (IsNegationOf(binary.Left, binary.Right) || IsNegationOf(binary.Right, binary.Left))
                            return orZero || IsKnownNonZero(binary.Left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                        return false;
                    case LlvmOpcode.Add:
                        if (orZero || (useInstructionInfo && binary.HasNoUnsignedWrap) || (useInstructionInfo && binary.HasNoSignedWrap))
                        {
                            if ((IsAndOf(binary.Left, binary.Right) && Power(binary.Right)) ||
                                (IsAndOf(binary.Right, binary.Left) && Power(binary.Left))) return true;
                            var lhs = Compute(binary.Left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                            var rhs = Compute(binary.Right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                            if ((~(lhs.Zero & rhs.Zero)).IsPowerOf2 && (orZero || !lhs.One.IsZero || !rhs.One.IsZero)) return true;
                        }
                        return (orZero || (useInstructionInfo && binary.HasNoUnsignedWrap)) && binary.Right is LlvmConstantInt { IsOne: true } &&
                            binary.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmConstantInt { IsMinusOne: true } };
                }
                return false;
            case LlvmSelectInstruction select:
                return Power(select.TrueValue) && Power(select.FalseValue);
            case LlvmPhiInstruction phi:
                Count(metrics, "value_facts.power.phi");
                if (IsPowerOfTwoRecurrence(phi, orZero, depth, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null)) return true;
                uint incomingDepth = Math.Max(depth, MaxDepth - 1);
                for (int n = 0; n < phi.IncomingCount; n++)
                    if (!ReferenceEquals(phi.GetIncomingValue(n), phi) && !IsKnownToBeAPowerOfTwo(phi.GetIncomingValue(n), orZero,
                        incomingDepth, phi.GetIncomingBlock(n).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null)) return false;
                return true;
            case LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } intrinsic } call when IsMinMaxIntrinsicKind(intrinsic.Kind):
                Count(metrics, "value_facts.power.minmax_intrinsic");
                return Power(call.GetArgument(1)) && Power(call.GetArgument(0));
            default:
                // Other intrinsic IDs and vscale attributes are not represented.
                Count(metrics, "value_facts.power.no_source_facts");
                return false;
        }
    }

    private static bool IsPowerOfTwoRecurrence(LlvmPhiInstruction phi, bool orZero, uint depth,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (!MatchSimpleRecurrence(phi, out var binary, out var start, out var step)) return false;
        for (int n = 0; n < phi.IncomingCount; n++)
            if (ReferenceEquals(phi.GetIncomingValue(n), start) && !IsKnownToBeAPowerOfTwo(start, orZero, depth,
                phi.GetIncomingBlock(n).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return false;
        if (binary.Opcode != LlvmOpcode.Mul && !ReferenceEquals(binary.Right, step)) return false;
        var context = binary.Parent!.GetTerminator();
        bool StepPower(bool zero) => IsKnownToBeAPowerOfTwo(step, zero, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        bool positiveStart = start is LlvmConstantInt c && c.Value.IsPowerOf2 && !c.Value.IsMinSignedValue;
        return binary.Opcode switch
        {
            LlvmOpcode.Mul => (orZero || (useInstructionInfo && binary.HasNoUnsignedWrap) || (useInstructionInfo && binary.HasNoSignedWrap)) && StepPower(orZero),
            LlvmOpcode.SDiv => positiveStart && (orZero || (useInstructionInfo && binary.IsExact)) && StepPower(false),
            LlvmOpcode.UDiv => (orZero || (useInstructionInfo && binary.IsExact)) && StepPower(false),
            LlvmOpcode.Shl => orZero || (useInstructionInfo && binary.HasNoUnsignedWrap) || (useInstructionInfo && binary.HasNoSignedWrap),
            LlvmOpcode.AShr => positiveStart && (orZero || (useInstructionInfo && binary.IsExact)),
            LlvmOpcode.LShr => orZero || (useInstructionInfo && binary.IsExact),
            _ => false
        };
    }

    private static bool MatchSimpleRecurrence(LlvmPhiInstruction phi, out LlvmBinaryOperator binary, out LlvmValue start, out LlvmValue step) =>
        LlvmValueTracking.MatchSimpleRecurrence(phi, out binary, out start, out step);
    private static bool IsNegationOf(LlvmValue value, LlvmValue other) => value is LlvmBinaryOperator
        { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } binary && ReferenceEquals(binary.Right, other);
    private static bool IsAndOf(LlvmValue value, LlvmValue other) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } binary &&
        (ReferenceEquals(binary.Left, other) || ReferenceEquals(binary.Right, other));
}
