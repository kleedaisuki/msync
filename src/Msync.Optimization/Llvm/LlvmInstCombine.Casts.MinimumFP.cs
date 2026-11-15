// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 2135–2243 (scalar paths).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool FitsInFPType(LlvmAPFloat value, LlvmAPFloatSemantics semantics) =>
            !value.Convert(semantics, LlvmRoundingMode.NearestTiesToEven).LosesInfo;
        private static LlvmType? ShrinkFPConstant(LlvmContext context, LlvmAPFloat value, bool preferBFloat)
        {
            if (preferBFloat && FitsInFPType(value, LlvmAPFloatSemantics.BFloat)) return LlvmType.GetBFloatTy(context);
            if (!preferBFloat && FitsInFPType(value, LlvmAPFloatSemantics.IEEEhalf)) return LlvmType.GetHalfTy(context);
            if (FitsInFPType(value, LlvmAPFloatSemantics.IEEEsingle)) return LlvmType.GetFloatTy(context);
            if (value.Semantics == LlvmAPFloatSemantics.IEEEdouble) return null;
            if (FitsInFPType(value, LlvmAPFloatSemantics.IEEEdouble)) return LlvmType.GetDoubleTy(context);
            return null;
        }
        private LlvmType GetMinimumFPType(LlvmValue value, LlvmType preferredType)
        {
            Count("casts.minimum_fp_type.queries");
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } extension) return extension.Operand.Type;
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } cast &&
                CanBeCastedExactlyIntToFP(cast.Operand, preferredType, cast.Opcode == LlvmOpcode.SIToFP, cast)) return preferredType;
            if (value is LlvmConstantFP constant && !value.Type.GetScalarType().IsPPC_FP128Ty() &&
                ShrinkFPConstant(value.Context, constant.Value, preferredType.GetScalarType().IsBFloatTy()) is { } type)
            {
                Count("casts.minimum_fp_type.constant_shrink");
                return type;
            }
            return value.Type;
        }
        private LlvmValue CreateFPCastWithFlags(LlvmValue value, LlvmType destination, LlvmFastMathFlags? flags = null)
        {
            var previous = Builder.FastMathFlags;
            if (flags is { } requested) Builder.FastMathFlags = requested;
            var opcode = value.Type.GetScalarSizeInBits() == destination.GetScalarSizeInBits() ? LlvmOpcode.BitCast :
                value.Type.GetScalarSizeInBits() > destination.GetScalarSizeInBits() ? LlvmOpcode.FPTrunc : LlvmOpcode.FPExt;
            var result = Builder.CreateCast(opcode, value, destination);
            Builder.FastMathFlags = previous;
            return result;
        }
    }
}
