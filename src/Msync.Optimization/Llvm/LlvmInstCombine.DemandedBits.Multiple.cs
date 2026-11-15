// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyMultipleUseDemandedBits.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        internal LlvmValue? SimplifyMultipleUseDemandedBits(LlvmInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            Count("demanded_bits.multiple_use_queries");
            uint width = demanded.BitWidth;
            LlvmKnownBits left = new(width), right = new(width);
            if (instruction is LlvmBinaryOperator binary && instruction.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)
            {
                right = ComputeDemandedKnownBits(binary.Right, query, depth + 1);
                left = ComputeDemandedKnownBits(binary.Left, query, depth + 1);
                known = AnalyzeDemandedBitwise(binary, left, right, query, depth);
                known = LlvmKnownBitsAnalysis.ComputeFromContext(instruction, known, depth, query.Context, query.Tree,
                    query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);
                if (DemandedSubset(demanded, known.Zero | known.One)) return DemandedConstant(instruction.Type, known.One);
                if (binary.Opcode == LlvmOpcode.And)
                {
                    if (DemandedSubset(demanded, left.Zero | right.One)) return binary.Left;
                    if (DemandedSubset(demanded, right.Zero | left.One)) return binary.Right;
                }
                else if (binary.Opcode == LlvmOpcode.Or)
                {
                    if (DemandedSubset(demanded, left.One | right.Zero)) return binary.Left;
                    if (DemandedSubset(demanded, right.One | left.Zero)) return binary.Right;
                }
                else
                {
                    if (DemandedSubset(demanded, right.Zero)) return binary.Left;
                    if (DemandedSubset(demanded, left.Zero)) return binary.Right;
                }
                return null;
            }
            if (instruction is LlvmBinaryOperator arithmetic && instruction.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub)
            {
                var fromOperands = LlvmAPInt.GetLowBitsSet(width, width - demanded.CountLeadingZeros());
                right = ComputeDemandedKnownBits(arithmetic.Right, query, depth + 1);
                if (DemandedSubset(fromOperands, right.Zero)) return arithmetic.Left;
                left = ComputeDemandedKnownBits(arithmetic.Left, query, depth + 1);
                if (arithmetic.Opcode == LlvmOpcode.Add && DemandedSubset(fromOperands, left.Zero)) return arithmetic.Right;
                known = LlvmKnownBits.AddSub(arithmetic.Opcode == LlvmOpcode.Add, arithmetic.HasNoSignedWrap,
                    arithmetic.HasNoUnsignedWrap, left, right);
                known = LlvmKnownBitsAnalysis.ComputeFromContext(instruction, known, depth, query.Context, query.Tree,
                    query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);
                return null;
            }
            known = ComputeDemandedKnownBits(instruction, query, depth);
            if (DemandedSubset(demanded, known.Zero | known.One)) return DemandedConstant(instruction.Type, known.One);
            if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt rightAmount,
                    Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt leftAmount } shift } &&
                rightAmount == leftAmount && rightAmount.Value.Ult(new LlvmAPInt(width, width)) &&
                DemandedSubset(demanded, LlvmAPInt.GetLowBitsSet(width, width - (uint)rightAmount.Value.GetZExtValue())))
                return shift.Left;
            return null;
        }
    }
}
