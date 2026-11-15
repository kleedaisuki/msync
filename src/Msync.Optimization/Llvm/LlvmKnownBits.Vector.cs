// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeKnownBits demanded elements; VectorUtils.cpp getShuffleDemandedElts.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmConstantInt? GetIntegerSplat(LlvmValue value, bool allowPoison = false) =>
        value as LlvmConstantInt ?? (value.Type.IsVectorTy() ? (value as LlvmConstant)?.GetSplatValue(allowPoison) as LlvmConstantInt : null);

    private static bool IsDemanded(LlvmAPInt mask, uint lane) => !(mask & LlvmAPInt.GetOneBitSet(mask.BitWidth, lane)).IsZero;

    private static LlvmAPInt GetAllDemandedElements(LlvmType type) =>
        LlvmAPInt.GetAllOnes(type is LlvmFixedVectorType vector ? vector.NumElements : 1);

    private static uint GetKnownBitWidth(LlvmType type, LlvmDataLayout layout)
    {
        LlvmType scalar = type.GetScalarType();
        return scalar.IsPointerTy() ? layout.GetPointerTypeSizeInBits(scalar) : scalar.GetIntegerBitWidth();
    }

    private static bool TryComputeVectorConstant(LlvmValue value, LlvmAPInt demand, uint width,
        Dictionary<string, ulong>? metrics, out LlvmKnownBits known)
    {
        known = new(width);
        if (value.Type is not LlvmVectorType || value is not LlvmConstant constant) return false;
        Debug.Assert(value.Type is not LlvmFixedVectorType fixedType || fixedType.NumElements == demand.BitWidth);
        if (constant.GetSplatValue(false) is LlvmConstantInt splat)
        {
            known = LlvmKnownBits.MakeConstant(splat.Value);
            Count(metrics, "known_bits.vector.constant_splat");
            return true;
        }
        if (constant is not (LlvmConstantDataVector or LlvmConstantVector)) return false;
        known = LlvmKnownBits.Conflict(width);
        for (uint lane = 0; lane < demand.BitWidth; ++lane)
        {
            if (!IsDemanded(demand, lane)) continue;
            Count(metrics, "known_bits.vector.constant_lane");
            if (constant is LlvmConstantDataVector data)
                known = known.IntersectWith(LlvmKnownBits.MakeConstant(data.GetElementAsAPInt(lane)));
            else
            {
                LlvmConstant? element = constant.GetAggregateElement(lane);
                if (element is LlvmPoisonValue) continue;
                if (element is not LlvmConstantInt integer) { known = new(width); return true; }
                known = known.IntersectWith(LlvmKnownBits.MakeConstant(integer.Value));
            }
        }
        if (known.HasConflict) known = new(width);
        return true;
    }

    private static LlvmKnownBits ComputeExtractElement(LlvmValue vector, LlvmValue index, BitsQuery query, uint width)
    {
        Count(query.Metrics, "known_bits.vector.extract");
        if (vector.Type is not LlvmFixedVectorType type) return new(width);
        LlvmAPInt demand = GetAllDemandedElements(type);
        if (index is LlvmConstantInt constant && constant.Value.GetLimitedValue() < type.NumElements)
            demand = LlvmAPInt.GetOneBitSet(type.NumElements, (uint)constant.Value.GetZExtValue());
        return query.Recurse(vector, demand);
    }

    private static LlvmKnownBits ComputeInsertElement(LlvmValue vector, LlvmValue element, LlvmValue index,
        LlvmAPInt demand, BitsQuery query, uint width)
    {
        Count(query.Metrics, "known_bits.vector.insert");
        if (vector.Type is LlvmScalableVectorType) return new(width);
        LlvmAPInt vectorDemand = demand;
        bool needsElement = true;
        if (index is LlvmConstantInt constant && constant.Value.GetLimitedValue() < demand.BitWidth)
        {
            uint lane = (uint)constant.Value.GetZExtValue();
            vectorDemand &= ~LlvmAPInt.GetOneBitSet(demand.BitWidth, lane);
            needsElement = IsDemanded(demand, lane);
        }
        LlvmKnownBits known = LlvmKnownBits.Conflict(width);
        if (needsElement)
        {
            known = query.Recurse(element);
            if (known.IsUnknown) return known;
        }
        if (!vectorDemand.IsZero) known = known.IntersectWith(query.Recurse(vector, vectorDemand));
        return known;
    }

    private static LlvmKnownBits ComputeShuffle(LlvmShuffleVectorInstruction shuffle, LlvmAPInt demand,
        BitsQuery query, uint width)
    {
        Count(query.Metrics, "known_bits.vector.shuffle");
        if (LlvmValueTracking.GetSplatValue(shuffle, query.Metrics) is { } splat) return query.Recurse(splat);
        LlvmAPInt leftDemand, rightDemand;
        if (shuffle.Type is LlvmScalableVectorType) leftDemand = rightDemand = demand;
        else
        {
            uint count = ((LlvmFixedVectorType)shuffle.Left.Type).NumElements;
            leftDemand = rightDemand = LlvmAPInt.GetZero(count);
            for (uint lane = 0; lane < demand.BitWidth; ++lane)
            {
                if (!IsDemanded(demand, lane)) continue;
                int mask = shuffle.GetMaskValue((int)lane);
                if (mask < 0) return new(width);
                if ((uint)mask < count) leftDemand |= LlvmAPInt.GetOneBitSet(count, (uint)mask);
                else rightDemand |= LlvmAPInt.GetOneBitSet(count, (uint)mask - count);
            }
        }
        LlvmKnownBits known = LlvmKnownBits.Conflict(width);
        if (!leftDemand.IsZero)
        {
            known = query.Recurse(shuffle.Left, leftDemand);
            if (known.IsUnknown) return known;
        }
        if (!rightDemand.IsZero) known = known.IntersectWith(query.Recurse(shuffle.Right, rightDemand));
        return known;
    }

    private static LlvmKnownBits ComputeVectorBitCast(LlvmValue operand, LlvmType resultType,
        LlvmAPInt demand, BitsQuery query, uint width)
    {
        Count(query.Metrics, "known_bits.vector.bitcast");
        var sourceType = (LlvmFixedVectorType)operand.Type;
        if (!sourceType.ElementType.IsIntegerTy() || resultType is LlvmScalableVectorType) return new(width);
        uint sourceWidth = sourceType.ElementType.GetIntegerBitWidth(), lanes = demand.BitWidth;
        bool littleEndian = query.Layout!.IsLittleEndian;
        LlvmKnownBits known = new(width);
        if (width % sourceWidth == 0)
        {
            uint scale = width / sourceWidth;
            LlvmAPInt subDemand = LlvmAPInt.GetZero(lanes * scale);
            for (uint lane = 0; lane < lanes; ++lane)
                if (IsDemanded(demand, lane)) subDemand |= LlvmAPInt.GetOneBitSet(subDemand.BitWidth, lane * scale);
            for (uint part = 0; part < scale; ++part)
            {
                LlvmKnownBits sub = query.Recurse(operand, subDemand.Shl(part));
                uint offset = (littleEndian ? part : scale - 1 - part) * sourceWidth;
                known = new(known.Zero.InsertBits(sub.Zero, offset), known.One.InsertBits(sub.One, offset));
            }
        }
        if (sourceWidth % width == 0)
        {
            uint scale = sourceWidth / width;
            LlvmAPInt subDemand = LlvmAPInt.GetZero(lanes / scale);
            for (uint lane = 0; lane < lanes; ++lane)
                if (IsDemanded(demand, lane)) subDemand |= LlvmAPInt.GetOneBitSet(subDemand.BitWidth, lane / scale);
            LlvmKnownBits sub = query.Recurse(operand, subDemand);
            known = LlvmKnownBits.Conflict(width);
            for (uint lane = 0; lane < lanes; ++lane)
            {
                if (!IsDemanded(demand, lane)) continue;
                uint offset = ((littleEndian ? lane : lanes - 1 - lane) % scale) * width;
                known = known.IntersectWith(new(sub.Zero.ExtractBits(width, offset), sub.One.ExtractBits(width, offset)));
                if (known.IsUnknown) break;
            }
        }
        return known;
    }

    private static LlvmKnownBits ComputeVectorConstantExpression(LlvmConstantExpr expression,
        LlvmAPInt demand, BitsQuery query, uint width) => expression.Opcode switch
    {
        LlvmOpcode.Add or LlvmOpcode.Sub => LlvmKnownBits.AddSub(expression.Opcode == LlvmOpcode.Add,
            query.UseInstructionInfo && expression.HasNoSignedWrap, query.UseInstructionInfo && expression.HasNoUnsignedWrap,
            query.Recurse(expression.GetConstantOperand(0)), query.Recurse(expression.GetConstantOperand(1))),
        LlvmOpcode.Xor => query.Recurse(expression.GetConstantOperand(0)) ^ query.Recurse(expression.GetConstantOperand(1)),
        LlvmOpcode.Trunc or LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr or LlvmOpcode.IntToPtr =>
            query.Recurse(expression.GetConstantOperand(0)).ZExtOrTrunc(width),
        LlvmOpcode.BitCast when expression.GetConstantOperand(0).Type.IsIntegerTy() || expression.GetConstantOperand(0).Type.IsPointerTy() =>
            query.Recurse(expression.GetConstantOperand(0)),
        LlvmOpcode.GetElementPtr => ComputeGep(expression, expression.SourceElementType!, query.Depth, query.Context, query.Tree,
            query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.Conditions, query.CondContext, query.Layout, demand),
        LlvmOpcode.ExtractElement => ComputeExtractElement(expression.GetConstantOperand(0), expression.GetConstantOperand(1), query, width),
        LlvmOpcode.InsertElement => ComputeInsertElement(expression.GetConstantOperand(0), expression.GetConstantOperand(1), expression.GetConstantOperand(2), demand, query, width),
        LlvmOpcode.BitCast when expression.GetConstantOperand(0).Type is LlvmFixedVectorType =>
            ComputeVectorBitCast(expression.GetConstantOperand(0), expression.Type, demand, query, width),
        // Source explicitly leaves ConstantExpr shuffles without operator facts.
        _ => new(width)
    };
}
