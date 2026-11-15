// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// ComputeNumSignBitsImpl, scalar and demanded fixed-vector integer paths.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static uint ComputeNumSignBits(LlvmValue value, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
        => ComputeNumSignBits(value, GetAllDemandedElements(value.Type), depth, context, tree, metrics,
            useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);

    public static uint ComputeNumSignBits(LlvmValue value, LlvmAPInt demandedElements, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null,
        bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null,
        LlvmDataLayout? layout = null)
    {
        Count(metrics, "sign_bits.visits");
        layout ??= GetQueryLayout(value, context);
        uint width = GetKnownBitWidth(value.Type, layout);
        if (depth >= MaxDepth)
        { Count(metrics, "sign_bits.depth_limit"); return 1; }
        uint Recurse(LlvmValue operand) => ComputeNumSignBits(operand, operand.Type is LlvmVectorType ? demandedElements : new LlvmAPInt(1, 1), depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        uint firstAnswer = 1;
        if (value is LlvmCastInstruction cast)
        {
            Count(metrics, "sign_bits.cast");
            if (cast.Opcode == LlvmOpcode.BitCast && value.Type is LlvmFixedVectorType && cast.Operand.Type.GetScalarType().IsIntegerTy())
            {
                uint sourceWidth = cast.Operand.Type.GetScalarSizeInBits();
                if (sourceWidth % width == 0 && ComputeNumSignBits(cast.Operand, depth + 1, context, tree, metrics,
                    useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout) == sourceWidth) return width;
            }
            if (cast.Opcode == LlvmOpcode.SExt)
                return Recurse(cast.Operand) + width - cast.Operand.Type.GetScalarSizeInBits();
            if (cast.Opcode == LlvmOpcode.Trunc)
            {
                uint bits = Recurse(cast.Operand), removed = cast.Operand.Type.GetScalarSizeInBits() - width;
                return bits > removed ? bits - removed : 1;
            }
        }
        if (value is LlvmBinaryOperator binary)
        {
            Count(metrics, "sign_bits.binary");
            uint? answer = ComputeBinarySignBits(binary, width, demandedElements, layout, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, out firstAnswer);
            if (answer.HasValue) return answer.Value;
        }
        if (value is LlvmSelectInstruction select)
        {
            Count(metrics, "sign_bits.select");
            if (LlvmSelectPattern.TrySignedMinMaxClamp(select, out _, out LlvmAPInt low, out LlvmAPInt high, metrics))
            { Count(metrics, "sign_bits.select.clamp"); return Math.Min(SignBitsOf(low), SignBitsOf(high)); }
            uint trueBits = Recurse(select.TrueValue);
            if (trueBits != 1) return Math.Min(trueBits, Recurse(select.FalseValue));
        }
        if (value is LlvmCallInstruction call && LlvmSelectPattern.TrySignedMinMaxIntrinsicClamp(call, out var lowBound, out var highBound, metrics))
        { Count(metrics, "sign_bits.intrinsic.clamp"); return Math.Min(SignBitsOf(lowBound), SignBitsOf(highBound)); }
        if (value is LlvmPhiInstruction phi && phi.IncomingCount is > 0 and <= 4)
        {
            Count(metrics, "sign_bits.phi");
            uint bits = width;
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                if (bits == 1) return bits;
                Count(metrics, "sign_bits.phi.incoming");
                bits = Math.Min(bits, ComputeNumSignBits(phi.GetIncomingValue(index), demandedElements, depth + 1,
                    phi.GetIncomingBlock(index).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout));
            }
            return bits;
        }
        if (value is LlvmExtractElementInstruction extract)
        {
            Count(metrics, "sign_bits.vector.extract");
            return ComputeNumSignBits(extract.VectorOperand, depth + 1, context, tree, metrics,
                useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        }
        if (value is LlvmShuffleVectorInstruction shuffle && ComputeShuffleSignBits(shuffle, demandedElements,
            depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout) is { } shuffleBits)
            return shuffleBits;
        if (ComputeVectorConstantSignBits(value, demandedElements, width, metrics) is { } constantBits) return constantBits;
        Count(metrics, "sign_bits.known_bits_fallback");
        return Math.Max(firstAnswer, Compute(value, demandedElements, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout).CountMinSignBits());
    }

    public static uint ComputeMaxSignificantBits(LlvmValue value, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null) =>
        GetKnownBitWidth(value.Type, layout ?? GetQueryLayout(value, context)) - ComputeNumSignBits(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout) + 1;

    private static uint SignBitsOf(LlvmAPInt value) => value.IsNegative ? value.CountLeadingOnes() : value.CountLeadingZeros();

    private static uint? ComputeBinarySignBits(LlvmBinaryOperator binary, uint width, LlvmAPInt demandedElements, LlvmDataLayout layout, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions, LlvmCondContext? condContext, out uint firstAnswer)
    {
        firstAnswer = 1;
        uint Recurse(LlvmValue operand) => ComputeNumSignBits(operand, operand.Type is LlvmVectorType ? demandedElements : new LlvmAPInt(1, 1), depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        LlvmKnownBits Known(LlvmValue operand) => Compute(operand, demandedElements, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        LlvmAPInt? constant = GetIntegerSplat(binary.Right)?.Value;
        uint left, right;
        switch (binary.Opcode)
        {
            case LlvmOpcode.SDiv:
                if (constant is { IsStrictlyPositive: true } denominator)
                    return Math.Min(width, Recurse(binary.Left) + denominator.GetActiveBits() - 1);
                break;
            case LlvmOpcode.SRem:
                left = Recurse(binary.Left);
                if (constant is { IsStrictlyPositive: true } divisor)
                    left = Math.Max(left, width - divisor.GetActiveBits() + (divisor.IsPowerOf2 ? 1U : 0U));
                return left;
            case LlvmOpcode.AShr:
                left = Recurse(binary.Left);
                if (constant is { } shift)
                {
                    if (shift.GetLimitedValue(width) >= width) break;
                    left = Math.Min(width, left + (uint)shift.GetZExtValue());
                }
                return left;
            case LlvmOpcode.Shl:
                if (constant is not { } amount || amount.GetLimitedValue(width) >= width) break;
                uint shiftAmount = (uint)amount.GetZExtValue();
                if (binary.Left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension &&
                    shiftAmount >= width - extension.Operand.Type.GetScalarSizeInBits())
                    left = Recurse(extension.Operand) + width - extension.Operand.Type.GetScalarSizeInBits();
                else left = Recurse(binary.Left);
                if (shiftAmount < left) return left - shiftAmount;
                break;
            case LlvmOpcode.And:
            case LlvmOpcode.Or:
            case LlvmOpcode.Xor:
                left = Recurse(binary.Left);
                if (left != 1) firstAnswer = Math.Min(left, Recurse(binary.Right));
                break;
            case LlvmOpcode.Add:
                left = Recurse(binary.Left);
                if (left == 1) break;
                if (constant is { IsAllOnes: true })
                {
                    LlvmKnownBits known = Known(binary.Left);
                    if ((known.Zero | new LlvmAPInt(width, 1)).IsAllOnes) return width;
                    if (known.IsNonNegative) return left;
                }
                right = Recurse(binary.Right);
                if (right != 1) return Math.Min(left, right) - 1;
                break;
            case LlvmOpcode.Sub:
                right = Recurse(binary.Right);
                if (right == 1) break;
                if (binary.Left is LlvmConstant { IsNullValue: true })
                {
                    LlvmKnownBits known = Known(binary.Right);
                    if ((known.Zero | new LlvmAPInt(width, 1)).IsAllOnes) return width;
                    if (known.IsNonNegative) return right;
                }
                left = Recurse(binary.Left);
                if (left != 1) return Math.Min(left, right) - 1;
                break;
            case LlvmOpcode.Mul:
                left = Recurse(binary.Left);
                if (left == 1) break;
                right = Recurse(binary.Right);
                if (right == 1) break;
                uint valid = (width - left + 1) + (width - right + 1);
                return valid > width ? 1 : width - valid + 1;
        }
        return null;
    }
}
