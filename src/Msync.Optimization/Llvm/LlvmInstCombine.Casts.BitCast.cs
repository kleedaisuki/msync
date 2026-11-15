// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 3230–3286, 3474–3636 (scalar paths).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldBitCastSelect(LlvmCastInstruction instruction)
        {
            if (instruction.Operand is not LlvmSelectInstruction { HasOneUse: true } select) return null;
            var destination = instruction.Type;
            var source = select.TrueValue.Type;
            if (select.Condition.Type is LlvmVectorType condition &&
                (destination is not LlvmVectorType vector || condition.ElementCount != vector.ElementCount)) return null;
            if ((select.TrueValue is LlvmConstant || select.FalseValue is LlvmConstant) &&
                (destination is not LlvmVectorType destinationVector || source is LlvmVectorType sourceVector &&
                    destinationVector.ElementCount.IsScalable == sourceVector.ElementCount.IsScalable &&
                    destinationVector.ElementCount.KnownMinValue <= sourceVector.ElementCount.KnownMinValue))
                return new LlvmSelectInstruction(select.Condition, Builder.CreateBitCast(select.TrueValue, destination),
                    Builder.CreateBitCast(select.FalseValue, destination)) { Source = select.Source };
            if (destination.IsVectorTy() != source.IsVectorTy()) return null;
            if (select.TrueValue is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true } first &&
                first.Operand.Type == destination && first.Operand is not LlvmConstant)
                return new LlvmSelectInstruction(select.Condition, first.Operand, Builder.CreateBitCast(select.FalseValue, destination)) { Source = select.Source };
            if (select.FalseValue is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true } second &&
                second.Operand.Type == destination && second.Operand is not LlvmConstant)
                return new LlvmSelectInstruction(select.Condition, Builder.CreateBitCast(select.TrueValue, destination), second.Operand) { Source = select.Source };
            return null;
        }
        private LlvmValue? FoldCopySignIdioms(LlvmCastInstruction instruction)
        {
            if (!instruction.Type.IsFloatingPointTy() || instruction.Operand is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } operation) return null;
            if (TryCopySignArm(operation.Left, operation.Right, instruction) is { } left) return left;
            return TryCopySignArm(operation.Right, operation.Left, instruction);
        }
        private LlvmValue? TryCopySignArm(LlvmValue first, LlvmValue magnitude, LlvmCastInstruction instruction)
        {
            if (first is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } logic) return null;
            if (logic.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } source ||
                logic.Right is not LlvmConstantInt mask || !mask.Value.IsMinSignedValue || source.Operand.Type != instruction.Type ||
                !IsKnownNonNegative(magnitude, instruction)) return null;
            Count("casts.bitcast.copysign");
            return Builder.CreateCopySign(Builder.CreateBitCast(magnitude, instruction.Type), source.Operand);
        }
        private LlvmInstruction? VisitBitCast(LlvmCastInstruction instruction)
        {
            Count("casts.bitcast.queries");
            if (instruction.Operand.Type == instruction.Type) return ReplaceInstUsesWith(instruction, instruction.Operand);
            if (instruction.Operand is LlvmPhiInstruction phi && OptimizeBitCastFromPhi(instruction, phi) is { } rewritten) return rewritten;
            if (FoldBitCastSelect(instruction) is { } select) { Count("casts.bitcast.select"); return select; }
            if (FoldCopySignIdioms(instruction) is { } sign) return ReplaceInstUsesWith(instruction, sign);
            return CommonCastTransforms(instruction);
        }
    }
}
