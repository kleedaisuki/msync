// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private enum ShiftSemantics { Lossy, Signed, Unsigned }
        private static bool IsShiftOp(LlvmOpcode opcode) => opcode is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr;
        private static bool IsRightShiftOp(LlvmOpcode opcode) => opcode is LlvmOpcode.LShr or LlvmOpcode.AShr;
        private static LlvmConstantInt ShiftInt(LlvmType type, ulong value) => LlvmConstantInt.Get(type.Context, new(type.GetScalarSizeInBits(), value));
        private static LlvmConstantInt ShiftInt(LlvmType type, LlvmAPInt value) => LlvmConstantInt.Get(type.Context, value);
        private static LlvmValue ShiftZExtOrSelf(LlvmValue value) => value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast ? cast.Operand : value;
        private static bool ShiftNot(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt { IsMinusOne: true } } binary)
            { operand = binary.Left; return true; }
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmConstantInt { IsMinusOne: true } } reversed)
            { operand = reversed.Right; return true; }
            operand = value; return false;
        }
        private static bool ShiftNeg(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } binary)
            { operand = binary.Right; return true; }
            operand = value; return false;
        }
        private static bool ShiftIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, out LlvmCallBase call)
        {
            if (value is LlvmCallBase candidate && candidate.CalledFunction?.IntrinsicDescriptor?.Kind == kind)
            { call = candidate; return true; }
            call = null!; return false;
        }
        private LlvmKnownBits ShiftKnown(LlvmValue value, LlvmInstruction context) =>
            LlvmKnownBitsAnalysis.Compute(value, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals);
        private bool ShiftMaskedZero(LlvmValue value, LlvmAPInt mask, LlvmInstruction context) =>
            LlvmKnownBitsAnalysis.MaskedValueIsZero(value, mask, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals);
        private LlvmInstruction ShiftResult(LlvmInstruction result, string decision)
        { Count(decision); return result; }
        private LlvmValue? SimplifyShiftAmount(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmInstruction context)
        {
            return LlvmInstructionSimplify.SimplifyBinOp(opcode, left, right, Query(context));
        }
        private static bool CanTryToConstantAddTwoShiftAmounts(LlvmValue shift0, LlvmValue amount0, LlvmValue shift1, LlvmValue amount1)
        {
            if (!ReferenceEquals(amount0.Type, amount1.Type)) return false;
            uint maximum = shift0.Type.GetScalarSizeInBits() - 1 + shift1.Type.GetScalarSizeInBits() - 1;
            return LlvmAPInt.GetAllOnes(amount0.Type.GetScalarSizeInBits()).GetLimitedValue(maximum) >= maximum;
        }

        private LlvmValue? ReassociateShiftAmtsOfTwoSameDirectionShifts(LlvmBinaryOperator outer,
            LlvmInstructionSimplify.BinaryQuery query, bool analyzeForSignBitExtraction = false)
        {
            if (outer.Left is not LlvmInstruction source || !IsShiftOp(outer.Opcode)) return null;
            LlvmValue amount0 = ShiftZExtOrSelf(outer.Right);
            LlvmCastInstruction? truncation = source as LlvmCastInstruction;
            if (truncation?.Opcode != LlvmOpcode.Trunc) truncation = null;
            LlvmValue innerValue = truncation?.Operand ?? source;
            if (innerValue is not LlvmBinaryOperator inner || !IsShiftOp(inner.Opcode)) return null;
            LlvmValue amount1 = ShiftZExtOrSelf(inner.Right);
            if (!CanTryToConstantAddTwoShiftAmounts(outer, amount0, inner, amount1)) return null;
            bool twoRight = IsRightShiftOp(outer.Opcode) && IsRightShiftOp(inner.Opcode);
            if (analyzeForSignBitExtraction && !twoRight) return null;
            if (outer.Opcode != inner.Opcode && !analyzeForSignBitExtraction) return null;
            if (truncation is not null && !analyzeForSignBitExtraction && !outer.Left.HasOneUse && !outer.Right.HasOneUse) return null;
            LlvmValue? simplified = LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.Add, amount0, amount1, query with { Context = outer });
            if (simplified is not LlvmConstantInt combined) return null;
            uint sourceWidth = inner.Left.Type.GetScalarSizeInBits();
            if (combined.Value.GetLimitedValue(sourceWidth) >= sourceWidth) return null;
            if (twoRight && (truncation is not null || analyzeForSignBitExtraction))
            {
                if (combined.Value.GetZExtValue() != sourceWidth - 1) return null;
                if (analyzeForSignBitExtraction) return inner.Left;
            }
            LlvmConstant amount = combined;
            if (!ReferenceEquals(combined.Type, inner.Left.Type))
            {
                amount = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.ZExt, combined, inner.Left.Type, DL, Metrics)!;
                if (amount is null) return null;
            }
            var result = new LlvmBinaryOperator(outer.Opcode, inner.Left, amount);
            if (truncation is null)
            {
                if (outer.Opcode == LlvmOpcode.Shl)
                { result.HasNoUnsignedWrap = outer.HasNoUnsignedWrap && inner.HasNoUnsignedWrap; result.HasNoSignedWrap = outer.HasNoSignedWrap && inner.HasNoSignedWrap; }
                else result.IsExact = outer.IsExact && inner.IsExact;
            }
            Count("instcombine.shift.reassociate_amounts");
            if (truncation is null) return result;
            Builder.Insert(result);
            return new LlvmCastInstruction(LlvmOpcode.Trunc, result, outer.Type);
        }

        private bool SetShiftFlags(LlvmBinaryOperator instruction)
        {
            if (instruction.Opcode == LlvmOpcode.Shl)
            { if (instruction.HasNoUnsignedWrap && instruction.HasNoSignedWrap) return false; }
            else
            {
                if (instruction.IsExact) return false;
                if ((instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } left && ReferenceEquals(left.Right, instruction.Right)) ||
                    (ShiftIntrinsic(instruction.Right, LlvmIntrinsicKind.CTTZ, out var cttz) && ReferenceEquals(cttz.GetArgument(0), instruction.Left)))
                { instruction.IsExact = true; Count("instcombine.shift.flags.exact_structural"); return true; }
            }
            LlvmKnownBits count = ShiftKnown(instruction.Right, instruction);
            ulong maximum = count.GetMaxValue().GetLimitedValue(count.BitWidth - 1);
            LlvmKnownBits known = ShiftKnown(instruction.Left, instruction);
            if (instruction.Opcode != LlvmOpcode.Shl)
            {
                bool exact = maximum <= known.CountMinTrailingZeros();
                instruction.IsExact = exact;
                if (exact) Count("instcombine.shift.flags.exact_knownbits");
                return exact;
            }
            bool changed = false;
            if (!instruction.HasNoUnsignedWrap && maximum <= known.CountMinLeadingZeros())
            { instruction.HasNoUnsignedWrap = true; changed = true; Count("instcombine.shift.flags.nuw"); }
            if (!instruction.HasNoSignedWrap && (maximum < known.CountMinSignBits() || maximum < LlvmKnownBitsAnalysis.ComputeNumSignBits(
                instruction.Left, context: instruction, tree: DT, metrics: Metrics, useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals)))
            { instruction.HasNoSignedWrap = true; changed = true; Count("instcombine.shift.flags.nsw"); }
            return changed;
        }
    }
}
