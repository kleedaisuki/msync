// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedUseBits Trunc/ZExt/SExt cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyDemandedCast(LlvmCastInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth, sourceWidth = instruction.Operand.Type.GetIntegerBitWidth();
            if (instruction.Opcode == LlvmOpcode.Trunc &&
                instruction.Operand is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt amount } shift &&
                amount.Value.Ult(new LlvmAPInt(sourceWidth, width)) &&
                amount.Value.Ule(new LlvmAPInt(sourceWidth, demanded.CountLeadingZeros())))
            {
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(instruction);
                var truncate = Builder.CreateTrunc(shift.Left, instruction.Type);
                return Builder.CreateBinary(LlvmOpcode.LShr, truncate,
                    LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, amount.Value.GetZExtValue()));
            }
            var inputDemand = demanded.ZExtOrTrunc(sourceWidth);
            if (instruction.Opcode == LlvmOpcode.SExt && demanded.GetActiveBits() > sourceWidth)
                inputDemand |= LlvmAPInt.GetSignedMinValue(sourceWidth);
            LlvmKnownBits inputKnown = new(sourceWidth);
            if (SimplifyDemandedBits(instruction, 0, inputDemand, ref inputKnown, query, depth + 1))
            {
                if (instruction.Opcode is LlvmOpcode.Trunc or LlvmOpcode.ZExt) instruction.DropPoisonGeneratingFlags();
                return instruction;
            }
            if (instruction.Opcode == LlvmOpcode.SExt)
            {
                if (inputKnown.IsNonNegative || demanded.GetActiveBits() <= sourceWidth)
                {
                    var zeroExtend = new LlvmCastInstruction(LlvmOpcode.ZExt, instruction.Operand, instruction.Type);
                    zeroExtend.TakeName(instruction);
                    return InsertNewInstWith(zeroExtend, instruction);
                }
                known = inputKnown.SExt(width);
                return null;
            }
            if (instruction.Opcode == LlvmOpcode.ZExt && instruction.IsNonNegative && !inputKnown.IsNegative)
                inputKnown = new(inputKnown.Zero | LlvmAPInt.GetSignedMinValue(sourceWidth), inputKnown.One);
            known = inputKnown.ZExtOrTrunc(width);
            return null;
        }
    }
}
