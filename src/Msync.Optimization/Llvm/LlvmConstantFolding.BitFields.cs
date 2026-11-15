// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldBitInsertOperands/ConstantFoldBitExtractOperands.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldBitInsertOperands(LlvmConstant @base, LlvmConstant value,
        LlvmConstant offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.bitinsert.queries");
        if (offset is LlvmUndefValue) return LlvmPoisonValue.Get(@base.Type);
        if (@base is LlvmPoisonValue && value is LlvmPoisonValue) return @base;
        if (@base.Type.IsVectorTy()) return null;
        if (offset is not LlvmConstantInt position) return null;
        ulong baseBits = layout.GetTypeSizeInBits(@base.Type).GetFixedValue();
        ulong valueBits = layout.GetTypeSizeInBits(value.Type).GetFixedValue();
        ulong start = position.GetZExtValue();
        if (unchecked(start + valueBits) > baseBits) return LlvmPoisonValue.Get(@base.Type);
        if (valueBits == baseBits) return ConstantFoldCastOperand(LlvmOpcode.BitCast, value, @base.Type, layout, metrics);
        if (@base is LlvmUndefValue && value is LlvmUndefValue) return LlvmUndefValue.Get(@base.Type);
        if (@base is not LlvmConstantByte bytes || value.Type.IsPointerTy()) return null;
        var integer = LlvmIntegerType.Get(@base.Context, (uint)valueBits);
        if (ConstantFoldCastOperand(LlvmOpcode.BitCast, value, integer, layout, metrics) is not LlvmConstantInt bits) return null;
        Count(metrics, "constant_fold.bitinsert.folded");
        return LlvmConstantByte.Get(@base.Context, bytes.Value.InsertBits(bits.Value, (uint)start));
    }

    public static LlvmConstant? ConstantFoldBitExtractOperands(LlvmType type, LlvmConstant source,
        LlvmConstant offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.bitextract.queries");
        if (source is LlvmPoisonValue || offset is LlvmUndefValue) return LlvmPoisonValue.Get(type);
        if (type.IsVectorTy()) return null;
        if (offset is not LlvmConstantInt position) return null;
        ulong count = layout.GetTypeSizeInBits(type).GetFixedValue();
        ulong start = position.GetZExtValue();
        if (unchecked(start + count) > layout.GetTypeSizeInBits(source.Type).GetFixedValue()) return LlvmPoisonValue.Get(type);
        if (source is LlvmUndefValue) return LlvmUndefValue.Get(type);
        if (source is not LlvmConstantByte bytes || type.IsPointerTy()) return null;
        LlvmConstant bits = LlvmConstantInt.Get(source.Context, bytes.Value.ExtractBits((uint)count, (uint)start));
        Count(metrics, "constant_fold.bitextract.folded");
        return ConstantFoldCastOperand(LlvmOpcode.BitCast, bits, type, layout, metrics);
    }
}
