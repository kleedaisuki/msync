// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// ConstantFoldConstantImpl, SymbolicallyEvaluateGEP/Binop, ConstantFoldCastOperand.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant ConstantFoldConstant(LlvmConstant constant, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics = null)
    {
        if (constant is not LlvmConstantExpr && constant is not LlvmConstantVector) return constant;
        return FoldConstantExpression(constant, layout, new(ReferenceEqualityComparer.Instance), metrics);
    }

    private static LlvmConstant FoldConstantOperand(LlvmConstant constant, LlvmDataLayout layout,
        ref Dictionary<LlvmConstant, LlvmConstant>? folded, Dictionary<string, ulong>? metrics)
    {
        if (constant is not LlvmConstantExpr && constant is not LlvmConstantVector) return constant;
        folded ??= new(ReferenceEqualityComparer.Instance);
        return FoldConstantExpression(constant, layout, folded, metrics);
    }

    private static LlvmConstant FoldConstantExpression(LlvmConstant constant, LlvmDataLayout layout,
        Dictionary<LlvmConstant, LlvmConstant> folded, Dictionary<string, ulong>? metrics)
    {
        if (constant is not LlvmConstantExpr && constant is not LlvmConstantVector) return constant;
        Count(metrics, "constant_fold.expression.visited");
        if (folded.TryGetValue(constant, out var cached))
        { Count(metrics, "constant_fold.expression.cache_hit"); return cached; }
        var operands = constant.Operands.Select(value =>
            FoldConstantExpression((LlvmConstant)value!, layout, folded, metrics)).ToArray();
        if (constant is LlvmConstantVector)
        {
            var vectorResult = LlvmConstantVector.Get(operands);
            folded.Add(constant, vectorResult);
            Count(metrics, "constant_fold.expression.vector_rebuilt");
            return vectorResult;
        }
        var expression = (LlvmConstantExpr)constant;
        LlvmConstant? result = expression.Opcode switch
        {
            LlvmOpcode.ExtractElement => LlvmConstantExpr.GetExtractElement(operands[0], operands[1]),
            LlvmOpcode.InsertElement => LlvmConstantExpr.GetInsertElement(operands[0], operands[1], operands[2]),
            LlvmOpcode.ShuffleVector => LlvmConstantExpr.GetShuffleVector(operands[0], operands[1], expression.ShuffleMask),
            _ => null
        };
        result ??= expression.IsCast
            ? ConstantFoldCastOperand(expression.Opcode, operands[0], expression.Type, layout, metrics)
            : expression.Opcode == LlvmOpcode.GetElementPtr
                ? ConstantFoldGetElementPtr(expression.SourceElementType!, operands[0], operands.Skip(1).ToArray(),
                    layout, expression.NoWrapFlags, expression.InRange, metrics)
                : ConstantFoldBinaryOpOperands(expression.Opcode, operands[0], operands[1], layout, metrics);
        result ??= constant;
        folded.Add(constant, result);
        Count(metrics, ReferenceEquals(result, constant) ? "constant_fold.expression.unchanged" : "constant_fold.expression.rewritten");
        return result;
    }

    public static LlvmConstant? ConstantFoldGetElementPtr(LlvmType source, LlvmConstant pointer,
        IReadOnlyList<LlvmConstant> indices, LlvmDataLayout layout, LlvmGepNoWrapFlags noWrap = default,
        LlvmConstantExprRange? inRange = null, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.gep.queries");
        if (indices.Count == 0) return pointer;
        if (!source.IsSized() || source.IsScalableTy())
        { Count(metrics, "constant_fold.gep.unsized_or_scalable"); return null; }
        var canonical = LlvmConstantExpr.GetGetElementPtr(layout, source, pointer, indices, noWrap, inRange);
        if (canonical is not LlvmConstantExpr { Opcode: LlvmOpcode.GetElementPtr } gep) return canonical;
        if (!pointer.Type.IsPointerTy()) return canonical;
        if (!LlvmGepOperator.TryGet(gep, out var view)) return canonical;
        uint width = layout.GetIndexTypeSizeInBits(pointer.Type);
        LlvmAPInt offset = new(width, 0);
        if (!LlvmValueTracking.AccumulateConstantOffset(view, layout, ref offset, metrics))
        { Count(metrics, "constant_fold.gep.symbolic_indices"); return canonical; }
        LlvmConstant current = gep.PointerOperand;
        noWrap = gep.NoWrapFlags;
        LlvmConstantRange? range = gep.InRange is { } initial
            ? new LlvmConstantRange(initial.Lower, initial.Upper).SExtOrTrunc(width) : null;
        bool overflow = false;
        while (LlvmGepOperator.TryGet(current, out var nested))
        {
            noWrap &= nested.NoWrapFlags;
            LlvmAPInt delta = new(width, 0);
            if (!LlvmValueTracking.AccumulateConstantOffset(nested, layout, ref delta, metrics)) break;
            if (((LlvmConstantExpr)current).InRange is { } nestedRange)
            {
                var adjusted = new LlvmConstantRange(nestedRange.Lower, nestedRange.Upper).SExtOrTrunc(width)
                    .Sub(new LlvmConstantRange(offset));
                range = range is { } existing ? existing.IntersectWith(adjusted) : adjusted;
            }
            current = (LlvmConstant)nested.PointerOperand;
            offset = offset.SAddOverflow(delta, out overflow);
            Count(metrics, "constant_fold.gep.nested_folded");
        }
        if (noWrap.HasNoUnsignedSignedWrap && !noWrap.IsInBounds && overflow)
        { noWrap = noWrap.WithoutNoUnsignedSignedWrap(); Count(metrics, "constant_fold.gep.nusw_dropped"); }
        LlvmAPInt baseInteger = new(layout.GetPointerTypeSizeInBits(current.Type), 0);
        if (current is LlvmConstantExpr { Opcode: LlvmOpcode.IntToPtr } integerPointer &&
            integerPointer.GetConstantOperand(0) is LlvmConstantInt address)
            baseInteger = address.Value.ZExtOrTrunc(baseInteger.BitWidth);
        if ((current.IsNullValue || !baseInteger.IsZero) && !layout.MustNotIntroduceIntToPtr(current.Type))
        {
            baseInteger = baseInteger.InsertBits(baseInteger.Trunc(width) + offset, 0);
            Count(metrics, "constant_fold.gep.numeric_address");
            return LlvmConstantExpr.GetIntToPtr(LlvmConstantInt.Get(pointer.Context, baseInteger), pointer.Type);
        }
        if (!noWrap.IsInBounds && offset.IsNonNegative && current is LlvmGlobalVariable global &&
            global.ValueType.IsSized() && global.Linkage != LlvmLinkage.ExternalWeakLinkage)
        {
            var extent = layout.GetTypeStoreSize(global.ValueType);
            if (!extent.IsScalable && extent.GetFixedValue() != 0 &&
                offset.Sle(new LlvmAPInt(width, extent.GetFixedValue(), implicitTrunc: true)))
            { noWrap |= LlvmGepNoWrapFlags.InBounds; Count(metrics, "constant_fold.gep.inbounds_inferred"); }
        }
        if (noWrap.HasNoUnsignedSignedWrap && offset.IsNonNegative) noWrap |= LlvmGepNoWrapFlags.NoUnsignedWrap;
        Count(metrics, "constant_fold.gep.ptradd_canonicalized");
        return LlvmConstantExpr.GetPtrAdd(current, LlvmConstantInt.Get(pointer.Context, offset), noWrap,
            range is { } finalRange ? new(finalRange.Lower, finalRange.Upper) : null);
    }

    public static LlvmConstant? ConstantFoldIntegerCast(LlvmConstant value, LlvmType destination,
        bool isSigned, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        if (value.Type == destination) return value;
        return ConstantFoldCastOperand(value.Type.GetScalarSizeInBits() > destination.GetScalarSizeInBits()
            ? LlvmOpcode.Trunc : isSigned ? LlvmOpcode.SExt : LlvmOpcode.ZExt, value, destination, layout, metrics);
    }

    private static LlvmConstant? FoldSymbolicCast(LlvmOpcode opcode, LlvmConstant value, LlvmType destination,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        if (value is not LlvmConstantExpr expression) return null;
        if (expression.IsCast && LlvmCastInstruction.IsEliminableCastPair(expression.Opcode, opcode,
            expression.GetConstantOperand(0).Type, value.Type, destination, layout) is { } combined)
        {
            Count(metrics, "constant_fold.cast.pair_eliminated");
            return ConstantFoldCastOperand(combined, expression.GetConstantOperand(0), destination, layout, metrics);
        }
        if (opcode is not (LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr)) return null;
        LlvmConstant? folded = null;
        if (expression.Opcode == LlvmOpcode.IntToPtr)
        {
            LlvmType intermediate = opcode == LlvmOpcode.PtrToInt ? layout.GetAddressType(value.Type) : layout.GetIntPtrType(value.Type);
            folded = ConstantFoldIntegerCast(expression.GetConstantOperand(0), intermediate, false, layout, metrics);
        }
        else if (expression.Opcode == LlvmOpcode.GetElementPtr)
        {
            LlvmAPInt offset = new(layout.GetIndexTypeSizeInBits(value.Type), 0);
            var baseValue = LlvmValueTracking.StripAndAccumulateConstantOffsets(value, layout, ref offset, metrics: metrics);
            if (baseValue is LlvmConstant { IsNullValue: true }) folded = LlvmConstantInt.Get(value.Context, offset);
            else if (expression.IndexCount == 1 && expression.SourceElementType!.IsIntegerTy(8) &&
                expression.GetIndex(0) is LlvmConstantExpr { Opcode: LlvmOpcode.Sub } subtraction &&
                subtraction.GetConstantOperand(0).IsNullValue &&
                subtraction.Type == layout.GetIndexType(value.Context, ((LlvmPointerType)value.Type).AddressSpace))
                folded = LlvmConstantExpr.GetSub(LlvmConstantExpr.GetCast(opcode, expression.PointerOperand, subtraction.Type),
                    subtraction.GetConstantOperand(1));
        }
        return folded is null ? null : ConstantFoldIntegerCast(folded, destination, false, layout, metrics);
    }

    public static bool IsConstantOffsetFromGlobal(LlvmConstant value, out LlvmGlobalValue? global,
        out LlvmAPInt offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.global_offset.visited");
        global = value as LlvmGlobalValue;
        offset = new(global is null ? 64 : layout.GetIndexTypeSizeInBits(global.Type), 0);
        if (global is not null) return true;
        if (value is not LlvmConstantExpr expression) return false;
        if (expression.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr)
            return IsConstantOffsetFromGlobal(expression.GetConstantOperand(0), out global, out offset, layout, metrics);
        if (expression.Opcode != LlvmOpcode.GetElementPtr ||
            !IsConstantOffsetFromGlobal(expression.PointerOperand, out global, out offset, layout, metrics)) return false;
        return LlvmValueTracking.AccumulateConstantOffset(LlvmGepOperator.Get(expression), layout, ref offset, metrics);
    }

    private static LlvmConstant? FoldSymbolicBinary(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        if (left is not LlvmConstantExpr && right is not LlvmConstantExpr) return null;
        if (left.Type.IsVectorTy()) return null;
        Count(metrics, "constant_fold.binary.symbolic_queries");
        if (opcode == LlvmOpcode.And)
        {
            var a = LlvmKnownBitsAnalysis.Compute(left, metrics: metrics);
            var b = LlvmKnownBitsAnalysis.Compute(right, metrics: metrics);
            if ((b.One | a.Zero).IsAllOnes) return left;
            if ((a.One | b.Zero).IsAllOnes) return right;
            var known = a & b;
            if (known.IsConstant) return LlvmConstantInt.Get(left.Context, known.GetConstant());
        }
        if (opcode == LlvmOpcode.Sub && IsConstantOffsetFromGlobal(left, out var first, out var firstOffset, layout, metrics) &&
            IsConstantOffsetFromGlobal(right, out var second, out var secondOffset, layout, metrics) && first == second)
        {
            uint width = left.Type.GetIntegerBitWidth();
            Count(metrics, "constant_fold.binary.same_global_difference");
            return LlvmConstantInt.Get(left.Context, firstOffset.ZExtOrTrunc(width) - secondOffset.ZExtOrTrunc(width));
        }
        return null;
    }
}
