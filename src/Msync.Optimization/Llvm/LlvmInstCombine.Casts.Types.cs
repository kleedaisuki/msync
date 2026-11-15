// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// isDesirableIntType/shouldChangeType and simplifyIntToPtrRoundTripCast.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool IsDesirableIntType(uint width) => width is 8 or 16 or 32 || DL.IsLegalInteger(width);
        private bool ShouldChangeType(uint from, uint to)
        {
            Count("casts.type_change.queries");
            bool fromLegal = from == 1 || DL.IsLegalInteger(from);
            bool toLegal = to == 1 || DL.IsLegalInteger(to);
            if (to < from && IsDesirableIntType(to)) return true;
            if ((fromLegal || IsDesirableIntType(from)) && !toLegal) return false;
            if (!fromLegal && !toLegal && to > from) return false;
            return true;
        }
        private bool ShouldChangeType(LlvmType from, LlvmType to) => from.IsIntegerTy() && to.IsIntegerTy()
            && ShouldChangeType(from.GetIntegerBitWidth(), to.GetIntegerBitWidth());
        private LlvmValue? SimplifyIntToPtrRoundTripCast(LlvmValue value)
        {
            if (value is not LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr } outer ||
                DL.GetTypeSizeInBits(outer.Type) != DL.GetTypeSizeInBits(outer.Operand.Type) ||
                outer.Operand is not LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } inner) return null;
            return outer.Type.GetScalarType() is LlvmPointerType destination && inner.Operand.Type.GetScalarType() is LlvmPointerType source &&
                destination.AddressSpace == source.AddressSpace && DL.GetTypeSizeInBits(inner.Operand.Type) == DL.GetTypeSizeInBits(inner.Type)
                ? inner.Operand : null;
        }
    }
}
