// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyGEPInst and simplifyLoadInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyScalarLoad(LlvmLoadInstruction load, LlvmValue pointer, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.load.queries");
        if (load.IsVolatile) return null;
        if (pointer is LlvmConstant constant)
            return LlvmConstantFolding.ConstantFoldLoadFromConstPtr(constant, load.Type, q.Layout, q.Metrics);
        if (LlvmValueTracking.GetUnderlyingObject(pointer, metrics: q.Metrics) is not LlvmGlobalVariable
            { IsConstant: true, HasDefinitiveInitializer: true } global) return null;
        if (LlvmConstantFolding.ConstantFoldLoadFromUniformValue(global.Initializer!, load.Type, q.Layout) is { } uniform) return uniform;
        LlvmAPInt offset = LlvmAPInt.GetZero(q.Layout.GetIndexTypeSizeInBits(pointer.Type));
        var stripped = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, q.Layout, ref offset,
            allowNonInbounds: true, allowInvariantGroup: true, metrics: q.Metrics);
        if (stripped != global) return null;
        offset = offset.SExtOrTrunc(q.Layout.GetIndexTypeSizeInBits(stripped.Type));
        return LlvmConstantFolding.ConstantFoldLoadFromConstPtr(global, load.Type, offset, q.Layout, q.Metrics);
    }

    private static LlvmValue? SimplifyScalarGep(LlvmGetElementPtrInstruction gep, LlvmValue pointer,
        IReadOnlyList<LlvmValue> indices, BinaryQuery q) =>
        SimplifyGepInst(gep.SourceElementType, pointer, indices, gep.NoWrapFlags, q);

    internal static LlvmValue? SimplifyGepInst(LlvmType sourceType, LlvmValue pointer,
        IReadOnlyList<LlvmValue> indices, LlvmGepNoWrapFlags flags, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.gep.queries");
        if (indices.Count == 0) return pointer;
        LlvmType lastType = LlvmGetElementPtrInstruction.GetIndexedType(sourceType, indices)!;
        LlvmType resultType = LlvmGetElementPtrInstruction.GetReturnType(pointer, indices);
        if (pointer.Type == resultType && indices.All(IsIntegerZero)) return pointer;
        if (pointer is LlvmPoisonValue || indices.Any(index => index is LlvmPoisonValue)) return LlvmPoisonValue.Get(resultType);
        if (q.IsUndef(pointer)) return LlvmUndefValue.Get(resultType);
        bool scalable = sourceType.IsScalableTy() || indices.Any(index => index.Type is LlvmScalableVectorType);
        if (indices.Count == 1 && !scalable && sourceType.IsSized())
        {
            ulong size = q.Layout.GetTypeAllocSize(sourceType).GetFixedValue();
            if (size == 0 && pointer.Type == resultType) return pointer;
            uint addressSpace = ((LlvmPointerType)pointer.Type.GetScalarType()).AddressSpace;
            if (indices[0].Type.GetScalarType().GetIntegerBitWidth() >= q.Layout.GetAddressSizeInBits(addressSpace) &&
                SimplifyPointerDifferenceIndex(indices[0], pointer, size, resultType, q) is { } difference) return difference;
        }
        if (!scalable && TryGepNegatedPointer(indices[^1], out var pointerOperand, out bool negative) &&
            q.Layout.GetTypeAllocSize(lastType).GetFixedValue() == 1 && indices.Take(indices.Count - 1).All(IsIntegerZero))
        {
            uint width = q.Layout.GetIndexSizeInBits(((LlvmPointerType)pointer.Type).AddressSpace);
            if (q.Layout.GetTypeSizeInBits(indices[^1].Type).GetFixedValue() == width)
            {
                LlvmAPInt baseOffset = LlvmAPInt.GetZero(width);
                var stripped = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, q.Layout, ref baseOffset,
                    allowNonInbounds: false, allowInvariantGroup: false, metrics: q.Metrics);
                var offset = negative ? baseOffset : baseOffset - new LlvmAPInt(width, 1);
                if (pointerOperand == stripped && !offset.IsZero)
                {
                    var integer = LlvmConstantInt.Get(pointer.Context, offset);
                    return LlvmConstantExpr.GetCast(LlvmOpcode.IntToPtr, integer, resultType);
                }
            }
        }
        if (pointer is not LlvmConstant constantPointer || indices.Any(index => index is not LlvmConstant)) return null;
        var constants = indices.Cast<LlvmConstant>().ToArray();
        LlvmConstant? expression = LlvmConstantExpr.GetGetElementPtr(q.Layout, sourceType, constantPointer, constants, flags);
        return expression is null
            ? LlvmConstantFolding.ConstantFoldGetElementPtr(sourceType, constantPointer, constants, q.Metrics)
            : LlvmConstantFolding.ConstantFoldConstant(expression, q.Layout, q.Metrics);
    }

    private static LlvmValue? SimplifyPointerDifferenceIndex(LlvmValue index, LlvmValue pointer, ulong elementSize,
        LlvmType resultType, BinaryQuery q)
    {
        LlvmValue? target = null;
        if (elementSize == 1) target = MatchGepPointerDifference(index, pointer);
        if (target is null && index is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, IsExact: true, Right: LlvmConstantInt amount } shift &&
            amount.Value.GetActiveBits() <= 64)
        {
            ulong count = amount.Value.GetZExtValue();
            // Type allocation sizes are uint64_t; no such size equals 2^64 or above.
            if (count < 64 && elementSize == 1UL << (int)count) target = MatchGepPointerDifference(shift.Left, pointer);
        }
        if (target is null && index is LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv, IsExact: true, Right: LlvmConstantInt size } division &&
            size.Value.GetActiveBits() <= 64 && size.Value.GetZExtValue() == elementSize) target = MatchGepPointerDifference(division.Left, pointer);
        return target is not null && target.Type == resultType &&
            LlvmValueTracking.GetUnderlyingObject(target, metrics: q.Metrics) == LlvmValueTracking.GetUnderlyingObject(pointer, metrics: q.Metrics)
            ? target : null;
    }

    private static LlvmValue? MatchGepPointerDifference(LlvmValue value, LlvmValue pointer) =>
        value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } subtraction &&
        subtraction.Left is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } lhs &&
        subtraction.Right is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } rhs && rhs.Operand == pointer
            ? lhs.Operand : null;

    private static bool TryGepNegatedPointer(LlvmValue value, out LlvmValue pointer, out bool negative)
    {
        negative = value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub && IsIntegerZero(sub.Left);
        var inner = negative ? ((LlvmBinaryOperator)value).Right : BitwiseNotOperand(value);
        if (inner is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } cast) { pointer = cast.Operand; return true; }
        pointer = null!;
        return false;
    }
}
