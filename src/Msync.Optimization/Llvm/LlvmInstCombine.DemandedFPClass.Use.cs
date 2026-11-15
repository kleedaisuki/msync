// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSimplifyDemanded.cpp
// SimplifyDemandedUseFPClass structural and sign operations.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyDemandedUseFPClass(LlvmInstruction instruction, C demanded,
            ref LlvmKnownFPClass known, LlvmInstructionSimplify.BinaryQuery query, uint depth = 0)
        {
            Count("demanded_fp.use_queries");
            var flags = instruction is ILlvmFastMathInstruction floating ? floating.FastMathFlags : default;
            demanded = AdjustFPDemand(demanded, flags);
            if (instruction is LlvmUnaryOperator negate)
            {
                if (negate.Operand.HasOneUse && LlvmKnownFPClasses.LookThroughFAbs(negate.Operand, out var source))
                {
                    var abs = (LlvmCallInstruction)negate.Operand;
                    if (SimplifyDemandedFPClass(abs, 0, UnknownSignFPDemand(demanded), out var sourceKnown, query, depth + 1)) return instruction;
                    C thisDemand = AdjustFPDemand(demanded, abs.FastMathFlags);
                    bool noSignedZeros = ((flags | abs.FastMathFlags) & LlvmFastMathFlags.NoSignedZeros) != 0;
                    if (DemandedFAbs(ref known, source, thisDemand, sourceKnown, noSignedZeros, true) is { } replacement) return replacement;
                    if ((thisDemand & C.NaN) == 0) sourceKnown.KnownNot(C.NaN);
                    if ((thisDemand & C.Infinity) == 0) sourceKnown.KnownNot(C.Infinity);
                    if (sourceKnown.SignBit == false || noSignedZeros && sourceKnown.IsKnownNever(LlvmKnownFPClass.OrderedLessThanZeroMask | C.NaN))
                        return ReplaceOperand(instruction, 0, source);
                }
                else
                {
                    if (SimplifyDemandedFPClass(instruction, 0, NegateFPDemand(demanded), out known, query, depth + 1)) return instruction;
                    known.Negate(); known.KnownNot(~demanded);
                }
            }
            else if (instruction is LlvmBinaryOperator binary && binary.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv)
                return DemandedFPArithmetic(binary, demanded, ref known, flags, query, depth);
            else if (instruction is LlvmCastInstruction cast && cast.Opcode is LlvmOpcode.FPExt or LlvmOpcode.FPTrunc)
                return DemandedFPCast(cast, demanded, ref known, flags, query, depth);
            else if (instruction is LlvmCallInstruction call)
                return DemandedFPCall(call, demanded, ref known, flags, query, depth);
            else if (instruction is LlvmSelectInstruction select)
            {
                if (SimplifyDemandedFPClass(instruction, 2, demanded, out var right, query, depth + 1) ||
                    SimplifyDemandedFPClass(instruction, 1, demanded, out var left, query, depth + 1)) return instruction;
                if (left.IsKnownNever(demanded)) return select.FalseValue;
                if (right.IsKnownNever(demanded)) return select.TrueValue;
                left = AdjustDemandedFPSelectArm(left, select, false, query, depth);
                right = AdjustDemandedFPSelectArm(right, select, true, query, depth);
                known = left.IntersectWith(right); known.KnownNot(~demanded);
            }
            else if (instruction is LlvmPhiInstruction phi)
            {
                if (depth >= DemandedFPMaxDepth - 2) return FPClassConstant(instruction.Type, known.GetKnownFPClasses());
                bool first = true, changed = false;
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    var predecessor = phi.GetIncomingBlock(index);
                    var incomingQuery = query.WithoutCondContext() with { Context = predecessor.GetTerminator() };
                    if (SimplifyDemandedFPClass(phi, index, demanded, out var incoming, incomingQuery, depth + 1))
                    {
                        var value = phi.GetIncomingValue(index);
                        for (int other = 0; other < phi.IncomingCount; ++other)
                            if (phi.GetIncomingBlock(other) == predecessor) phi.SetIncomingValue(other, value);
                        changed = true;
                    }
                    known = first ? incoming : known | incoming; first = false;
                }
                if (changed) return phi;
                known.KnownNot(~demanded);
            }
            else if (instruction.Opcode is LlvmOpcode.ExtractElement or LlvmOpcode.ExtractValue)
            {
                if (instruction is LlvmExtractValueInstruction extract && DemandedFPFrexpExtract(extract, demanded, ref known, query, depth, out var replacement)) return replacement;
                if (SimplifyDemandedFPClass(instruction, 0, demanded, out known, query, depth + 1)) return instruction;
                if (instruction.Opcode == LlvmOpcode.ExtractElement) known.KnownNot(~demanded);
            }
            else if (instruction.Opcode is LlvmOpcode.InsertElement or LlvmOpcode.ShuffleVector or LlvmOpcode.InsertValue)
            {
                int first = instruction.Opcode == LlvmOpcode.InsertValue ? 0 : 1, second = 1 - first;
                if (SimplifyDemandedFPClass(instruction, first, demanded, out var left, query, depth + 1) ||
                    SimplifyDemandedFPClass(instruction, second, demanded, out var right, query, depth + 1)) return instruction;
                known = left | right;
                if (instruction.Opcode != LlvmOpcode.InsertValue) known.KnownNot(~demanded);
            }
            else
            {
                known = ComputeDemandedFP(instruction, demanded, query, depth + 1);
                known.KnownNot(~demanded);
            }
            return FPClassConstant(instruction.Type, known.GetKnownFPClasses());
        }
    }
}
