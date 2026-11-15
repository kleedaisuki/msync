// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedInstructionBits/UseBits/Bits.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private const uint DemandedBitsMaxDepth = 6;
        private static bool DemandedSubset(LlvmAPInt value, LlvmAPInt superset) => (value & superset) == value;
        private static LlvmValue DemandedConstant(LlvmType type, LlvmAPInt bits) => LlvmConstantInt.Get(type.Context, bits);
        private static uint DemandedBitWidth(LlvmType type, LlvmDataLayout layout)
        {
            uint width = type.GetScalarSizeInBits();
            return width != 0 ? width : layout.GetPointerTypeSizeInBits(type);
        }

        private LlvmKnownBits ComputeDemandedKnownBits(LlvmValue value, LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmKnownBitsAnalysis.Compute(value, depth, query.Context, query.Tree, query.Metrics,
                query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);

        private LlvmKnownBits AnalyzeDemandedBitwise(LlvmBinaryOperator instruction, LlvmKnownBits left,
            LlvmKnownBits right, LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmKnownBitsAnalysis.AnalyzeKnownBitsFromAndXorOr(instruction, left, right, depth, query.Context,
                query.Tree, query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);

        internal bool SimplifyDemandedInstructionBits(LlvmInstruction instruction)
        {
            LlvmKnownBits known = new(DemandedBitWidth(instruction.Type, DL));
            return SimplifyDemandedInstructionBits(instruction, ref known);
        }

        internal bool SimplifyDemandedInstructionBits(LlvmInstruction instruction, ref LlvmKnownBits known)
        {
            Count("demanded_bits.instruction_queries");
            var result = SimplifyDemandedUseBits(instruction, LlvmAPInt.GetAllOnes(known.BitWidth), ref known, Query(instruction));
            if (result is null) return false;
            if (result != instruction) ReplaceInstUsesWith(instruction, result);
            Count("demanded_bits.instruction_rewrites");
            return true;
        }

        internal bool SimplifyDemandedBits(LlvmInstruction instruction, int operand, LlvmAPInt demanded,
            ref LlvmKnownBits known) => SimplifyDemandedBits(instruction, operand, demanded, ref known, Query(instruction));

        internal bool SimplifyDemandedBits(LlvmInstruction instruction, int operand, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth = 0)
        {
            Count("demanded_bits.operand_queries");
            var use = instruction.GetOperandUse(operand);
            var value = use.Value!;
            if (value is LlvmConstant)
            { known = ComputeDemandedKnownBits(value, query, depth); return false; }
            known = new(demanded.BitWidth);
            if (demanded.IsZero)
            {
                ReplaceUse(use, LlvmUndefValue.Get(value.Type));
                Count("demanded_bits.undemanded_operand");
                return true;
            }
            if (value is not LlvmInstruction source)
            { known = ComputeDemandedKnownBits(value, query, depth); return false; }
            if (depth == DemandedBitsMaxDepth)
            { Count("demanded_bits.depth_limit"); return false; }
            var replacement = source.HasOneUse
                ? SimplifyDemandedUseBits(source, demanded, ref known, query, depth)
                : SimplifyMultipleUseDemandedBits(source, demanded, ref known, query, depth);
            if (replacement is null) return false;
            ReplaceUse(use, replacement);
            Count("demanded_bits.operand_rewrites");
            return true;
        }

        private bool ShrinkDemandedConstant(LlvmInstruction instruction, int operand, LlvmAPInt demanded)
        {
            if (instruction.GetOperand(operand) is not LlvmConstantInt constant || DemandedSubset(constant.Value, demanded)) return false;
            instruction.SetOperand(operand, LlvmConstantInt.Get(constant.Context, constant.Value & demanded));
            Count("demanded_bits.constants_shrunk");
            return true;
        }

        internal LlvmValue? SimplifyDemandedUseBits(LlvmInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth = 0)
        {
            Debug.Assert(depth <= DemandedBitsMaxDepth && known.BitWidth == demanded.BitWidth);
            Count("demanded_bits.use_queries");
            LlvmValue? result;
            switch (instruction.Opcode)
            {
                case LlvmOpcode.And: case LlvmOpcode.Or: case LlvmOpcode.Xor:
                    result = SimplifyDemandedBitwise((LlvmBinaryOperator)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.Select:
                    result = SimplifyDemandedSelect((LlvmSelectInstruction)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.Trunc: case LlvmOpcode.ZExt: case LlvmOpcode.SExt:
                    result = SimplifyDemandedCast((LlvmCastInstruction)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.Add: case LlvmOpcode.Sub: case LlvmOpcode.Mul:
                    result = SimplifyDemandedArithmetic((LlvmBinaryOperator)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.Shl: case LlvmOpcode.LShr: case LlvmOpcode.AShr:
                    result = SimplifyDemandedShift((LlvmBinaryOperator)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.UDiv: case LlvmOpcode.SRem:
                    result = SimplifyDemandedDivision((LlvmBinaryOperator)instruction, demanded, ref known, query, depth); break;
                case LlvmOpcode.Call:
                    result = SimplifyDemandedCall((LlvmCallInstruction)instruction, demanded, ref known, query, depth); break;
                default:
                    known = ComputeDemandedKnownBits(instruction, query, depth); result = null; break;
            }
            if (result is not null) return result;
            if (instruction.Type.IsPointerTy())
            {
                uint zeros = (uint)System.Numerics.BitOperations.Log2(LlvmValueTracking.GetPointerAlignment(instruction, DL, Metrics).Value);
                known = new(known.Zero | LlvmAPInt.GetLowBitsSet(known.BitWidth, zeros), known.One);
            }
            if (!instruction.Type.IsPointerTy() && DemandedSubset(demanded, known.Zero | known.One))
            { Count("demanded_bits.known_constant"); return DemandedConstant(instruction.Type, known.One); }
            if (CLOpts.VerifyKnownBits)
            {
                var reference = ComputeDemandedKnownBits(instruction, query, depth);
                if (!known.Equals(reference)) throw new InvalidOperationException($"Mismatched demanded KnownBits for {instruction.Name} in {F.Name}: {known} != {reference}");
            }
            return null;
        }
    }
}
