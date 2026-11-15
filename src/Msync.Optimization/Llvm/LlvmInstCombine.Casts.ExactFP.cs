// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 2245–2303.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool CanBeCastedExactlyIntToFP(LlvmValue value, LlvmType floatingType, bool signed, LlvmInstruction context)
        {
            Count("casts.exact_int_to_fp.queries");
            int sourceSize = (int)value.Type.GetScalarSizeInBits() - (signed ? 1 : 0);
            int destinationBits = floatingType.GetFPMantissaWidth();
            if (sourceSize <= destinationBits) return true;
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.FPToUI or LlvmOpcode.FPToSI } cast)
            {
                int sourceBits = cast.Operand.Type.GetFPMantissaWidth();
                if (!signed && cast.Opcode == LlvmOpcode.FPToSI) sourceBits++;
                if (sourceBits > 0 && destinationBits > 0 && sourceBits <= destinationBits) return true;
            }
            var known = ComputeKnownBits(value, context);
            int significant = (int)value.Type.GetScalarSizeInBits() - (int)known.CountMinLeadingZeros() - (int)known.CountMinTrailingZeros();
            if (significant <= destinationBits) return true;
            return signed && (int)value.Type.GetScalarSizeInBits() - (int)ComputeNumSignBits(value, context) <= destinationBits;
        }
        private bool IsKnownExactCastIntToFP(LlvmCastInstruction instruction) =>
            CanBeCastedExactlyIntToFP(instruction.Operand, instruction.Type, instruction.Opcode == LlvmOpcode.SIToFP, instruction);
    }
}
