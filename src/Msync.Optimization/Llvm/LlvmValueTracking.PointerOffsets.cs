// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Value.cpp
// stripAndAccumulateConstantOffsets; IR/Operator.cpp accumulateConstantOffset.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static LlvmValue StripAndAccumulateConstantOffsets(LlvmValue value, LlvmDataLayout layout,
        ref LlvmAPInt offset, bool allowNonInbounds = true, bool allowInvariantGroup = false,
        Dictionary<string, ulong>? metrics = null, bool lookThroughIntToPtr = false,
        Func<LlvmValue, LlvmAPInt?>? externalAnalysis = null)
    {
        Count(metrics, "value_tracking.pointer_offsets.queries");
        if (!value.Type.IsPtrOrPtrVectorTy()) return value;
        uint width = offset.BitWidth;
        Debug.Assert(width == layout.GetIndexTypeSizeInBits(value.Type));
        HashSet<LlvmValue> visited = new(ReferenceEqualityComparer.Instance) { value };
        do
        {
            Count(metrics, "value_tracking.pointer_offsets.steps");
            if (LlvmGepOperator.TryGet(value, out var gep))
            {
                if (!allowNonInbounds && !gep.NoWrapFlags.IsInBounds)
                { Count(metrics, "value_tracking.pointer_offsets.not_inbounds"); return value; }
                var delta = LlvmAPInt.GetZero(layout.GetIndexTypeSizeInBits(gep.Type));
                if (!AccumulateConstantOffset(gep, layout, ref delta, metrics, externalAnalysis)) return value;
                if (delta.GetSignificantBits() > width)
                { Count(metrics, "value_tracking.pointer_offsets.width_stop"); return value; }
                delta = delta.SExtOrTrunc(width);
                if (externalAnalysis is null) offset += delta;
                else
                {
                    var sum = offset.SAddOverflow(delta, out bool overflow);
                    if (overflow) { Count(metrics, "value_tracking.pointer_offsets.external_overflow"); return value; }
                    offset = sum;
                }
                value = gep.PointerOperand;
            }
            else if (value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast)
            {
                if (!cast.Operand.Type.IsPtrOrPtrVectorTy()) return value;
                value = cast.Operand;
            }
            else if (value is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } addressCast)
                value = addressCast.Operand;
            else if (value is LlvmConstantExpr { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast } expression)
            {
                if (!expression.GetConstantOperand(0).Type.IsPtrOrPtrVectorTy()) return value;
                value = expression.GetConstantOperand(0);
            }
            else if (allowNonInbounds && lookThroughIntToPtr &&
                value is LlvmConstantExpr { Opcode: LlvmOpcode.IntToPtr } integerPointer &&
                integerPointer.GetConstantOperand(0).Type.GetScalarSizeInBits() == width &&
                integerPointer.GetConstantOperand(0) is LlvmConstantExpr { Opcode: LlvmOpcode.Add } addition &&
                addition.GetConstantOperand(0) is LlvmConstantExpr { Opcode: LlvmOpcode.PtrToInt } pointerCast &&
                addition.GetConstantOperand(1) is LlvmConstantInt displacement)
            {
                offset += displacement.Value;
                value = pointerCast.GetConstantOperand(0);
            }
            else if (value is LlvmCallBase)
            {
                // No returned-argument attributes or invariant-group intrinsic IDs are represented.
                Count(metrics, "value_tracking.pointer_offsets.call_no_source_facts");
                return value;
            }
            else if (value is LlvmInstruction)
            {
                if (!allowNonInbounds || !lookThroughIntToPtr ||
                    value is not LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr } intToPtr ||
                    intToPtr.Operand.Type.GetScalarSizeInBits() != width ||
                    intToPtr.Operand is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add,
                        Left: LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } pointer,
                        Right: LlvmConstantInt constant }) return value;
                offset += constant.Value;
                value = pointer.Operand;
            }
            else return value;
        } while (visited.Add(value));
        Count(metrics, "value_tracking.pointer_offsets.cycle");
        return value;
    }

    public static bool AccumulateConstantOffset(LlvmGetElementPtrInstruction gep, LlvmDataLayout layout,
        ref LlvmAPInt offset, Dictionary<string, ulong>? metrics = null,
        Func<LlvmValue, LlvmAPInt?>? externalAnalysis = null) =>
        AccumulateConstantOffset(LlvmGepOperator.Get(gep), layout, ref offset, metrics, externalAnalysis);

    public static bool AccumulateConstantOffset(LlvmGepOperator gep, LlvmDataLayout layout,
        ref LlvmAPInt offset, Dictionary<string, ulong>? metrics = null,
        Func<LlvmValue, LlvmAPInt?>? externalAnalysis = null)
    {
        Debug.Assert(offset.BitWidth == layout.GetIndexTypeSizeInBits(gep.Type));
        Count(metrics, "value_tracking.gep_offset.queries");
        if (gep.SourceElementType.IsIntegerTy(8) && gep.IndexCount != 0 && externalAnalysis is null)
        {
            Count(metrics, "value_tracking.gep_offset.byte_fast_path");
            if (gep.GetIndex(0) is not LlvmConstantInt integer || !integer.Type.IsIntegerTy()) return false;
            offset += integer.Value.SExtOrTrunc(offset.BitWidth);
            return true;
        }
        bool usedExternalAnalysis = false;
        LlvmType indexed = gep.SourceElementType;
        for (int n = 0; n < gep.IndexCount; n++)
        {
            Count(metrics, "value_tracking.gep_offset.indices");
            var index = gep.GetIndex(n);
            LlvmStructType? structure = n != 0 ? indexed as LlvmStructType : null;
            bool vectorElement = n != 0 && indexed is LlvmVectorType;
            LlvmType element = n == 0 ? indexed : LlvmGetElementPtrInstruction.GetTypeAtIndex(indexed, index)!;
            bool scalable = element.IsScalableTy();
            if (index is LlvmConstantInt constant && constant.Type.IsIntegerTy())
            {
                if (!constant.IsZero)
                {
                    if (scalable) { Count(metrics, "value_tracking.gep_offset.scalable_stop"); return false; }
                    var amount = structure is not null
                        ? new LlvmAPInt(offset.BitWidth, layout.GetStructLayout(structure).GetElementOffset((uint)constant.GetZExtValue()).GetFixedValue(), implicitTrunc: true)
                        : constant.Value;
                    ulong stride = structure is not null ? 1 : (vectorElement ? layout.GetTypeStoreSize(element) : layout.GetTypeAllocSize(element)).GetFixedValue();
                    if (!AccumulateGepIndex(ref offset, amount, stride, usedExternalAnalysis, metrics)) return false;
                }
            }
            else
            {
                if (externalAnalysis is null || structure is not null || scalable)
                { Count(metrics, "value_tracking.gep_offset.nonconstant_stop"); return false; }
                Count(metrics, "value_tracking.gep_offset.external_queries");
                if (externalAnalysis(index) is not { } amount)
                { Count(metrics, "value_tracking.gep_offset.external_no_fact"); return false; }
                usedExternalAnalysis = true;
                ulong stride = (vectorElement ? layout.GetTypeStoreSize(element) : layout.GetTypeAllocSize(element)).GetFixedValue();
                if (!AccumulateGepIndex(ref offset, amount, stride, usedExternalAnalysis, metrics)) return false;
            }
            indexed = structure is not null ? structure.GetElementType((int)((LlvmConstantInt)index).GetZExtValue()) : element;
        }
        return true;
    }

    private static bool AccumulateGepIndex(ref LlvmAPInt offset, LlvmAPInt index, ulong stride,
        bool usedExternalAnalysis, Dictionary<string, ulong>? metrics)
    {
        index = index.SExtOrTrunc(offset.BitWidth);
        var size = new LlvmAPInt(offset.BitWidth, stride, implicitTrunc: true);
        if (!usedExternalAnalysis) { offset += index * size; return true; }
        var product = index.SMulOverflow(size, out bool overflow);
        if (overflow) { Count(metrics, "value_tracking.gep_offset.external_mul_overflow"); return false; }
        offset = offset.SAddOverflow(product, out overflow);
        if (overflow) Count(metrics, "value_tracking.gep_offset.external_add_overflow");
        return !overflow;
    }
}
