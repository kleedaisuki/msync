// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// computeKnownBits and Support/KnownBits.cpp comparison transfer functions.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private const uint MaxDepth = 6;
    private readonly record struct BitsQuery(uint Depth, LlvmInstruction? Context, LlvmDominatorTree? Tree, Dictionary<string, ulong>? Metrics, bool UseInstructionInfo, LlvmAssumptionCache? Assumptions, bool AllowEphemerals, LlvmDomConditionCache? Conditions, LlvmCondContext? CondContext, LlvmDataLayout? Layout, LlvmAPInt? DemandedElements = null)
    {
        internal LlvmKnownBits Recurse(LlvmValue value) => Recurse(value, DemandedElements);
        internal LlvmKnownBits Recurse(LlvmValue value, LlvmAPInt? demanded) => Compute(value,
            value.Type is LlvmVectorType ? demanded ?? GetAllDemandedElements(value.Type) : new LlvmAPInt(1, 1),
            Depth + 1, Context, Tree, Metrics, UseInstructionInfo, Assumptions, AllowEphemerals, Conditions, CondContext, Layout);
    }
    public static LlvmKnownBits Compute(LlvmValue value, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
        => Compute(value, GetAllDemandedElements(value.Type), depth, context, tree, metrics, useInstructionInfo,
            assumptions, allowEphemerals, conditions, condContext, layout);

    public static LlvmKnownBits Compute(LlvmValue value, LlvmAPInt demandedElements, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false,
        LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        layout ??= GetQueryLayout(value, context);
        if (value.Type is LlvmVectorType) Count(metrics, "known_bits.vector.queries");
        if (demandedElements.IsZero)
        { Count(metrics, "known_bits.vector.empty_demand"); return new(GetKnownBitWidth(value.Type, layout)); }
        var known = ComputeOperatorFacts(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout, demandedElements);
        if (value.Type.IsPointerTy() && value is not (LlvmConstantPointerNull or LlvmConstantAggregateZero or LlvmUndefValue)
            && depth < MaxDepth)
        {
            uint zeros = (uint)BitOperations.TrailingZeroCount(LlvmValueTracking.GetPointerAlignment(value, layout, metrics).Value);
            known = new(known.Zero | LlvmAPInt.GetLowBitsSet(known.BitWidth, zeros), known.One);
            Count(metrics, "known_bits.pointer_alignment");
        }
        if (condContext is null && (assumptions is null && conditions is null || context is null) || value is LlvmConstantInt or LlvmConstantPointerNull or LlvmConstantAggregateZero or LlvmConstantVector or LlvmConstantDataVector or LlvmUndefValue || depth >= MaxDepth) return known;
        return ComputeFromContext(value, known, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
    }

    private static LlvmKnownBits ComputeOperatorFacts(LlvmValue value, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null, LlvmAPInt? demandedElements = null)
    {
        Count(metrics, "known_bits.visits");
        uint width = GetKnownBitWidth(value.Type, layout!);
        LlvmAPInt demand = demandedElements ?? GetAllDemandedElements(value.Type);
        if (TryComputeVectorConstant(value, demand, width, metrics, out var constantBits)) return constantBits;
        if (value is LlvmConstantInt integer) return LlvmKnownBits.MakeConstant(integer.Value);
        if (value is LlvmConstantPointerNull or LlvmConstantAggregateZero) return LlvmKnownBits.MakeConstant(LlvmAPInt.GetZero(width));
        if (value is LlvmUndefValue) return new(width);
        if (value is LlvmArgument argument && argument.Parent?.GetParamAttributes((int)argument.ArgNo).Range is { } range)
            return range.ToConstantRange().ToKnownBits();
        if (depth >= MaxDepth) { Count(metrics, "known_bits.depth_limit"); return new(width); }
        BitsQuery query = new(depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout, demand);
        switch (value)
        {
            case LlvmBinaryOperator binary:
                Count(metrics, "known_bits.binary");
                return ComputeBinary(binary, query, depth, context, tree, metrics);
            case LlvmExtractElementInstruction extract:
                return ComputeExtractElement(extract.VectorOperand, extract.IndexOperand, query, width);
            case LlvmInsertElementInstruction insert:
                return ComputeInsertElement(insert.VectorOperand, insert.InsertedElementOperand, insert.IndexOperand, demand, query, width);
            case LlvmShuffleVectorInstruction shuffle:
                return ComputeShuffle(shuffle, demand, query, width);
            case LlvmConstantExpr expression:
                return ComputeVectorConstantExpression(expression, demand, query, width);
            case LlvmCastInstruction cast:
                Count(metrics, "known_bits.cast");
                if (cast.Opcode is LlvmOpcode.ZExt or LlvmOpcode.Trunc or LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr or LlvmOpcode.IntToPtr)
                {
                    LlvmKnownBits bits = query.Recurse(cast.Operand);
                    if (cast.IsNonNegative && !bits.IsNegative) bits = bits.WithNonNegative();
                    return bits.ZExtOrTrunc(width);
                }
                if (cast.Opcode == LlvmOpcode.SExt) return query.Recurse(cast.Operand).SExt(width);
                if (cast.Opcode == LlvmOpcode.BitCast && cast.Operand.Type is LlvmFixedVectorType)
                    return ComputeVectorBitCast(cast.Operand, cast.Type, demand, query, width);
                if (cast.Opcode == LlvmOpcode.BitCast && (cast.Operand.Type.IsIntegerTy() || cast.Operand.Type.IsPointerTy())) return query.Recurse(cast.Operand);
                if (cast.Opcode == LlvmOpcode.BitCast && cast.Operand.Type.IsFloatingPointTy())
                    return LlvmKnownFPClasses.Compute(cast.Operand, contextInstruction: context, tree: tree, metrics: metrics, depth: (int)depth + 1, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext).ToKnownBits(cast.Operand.Type.GetScalarType().GetFltSemantics());
                if (cast.Opcode == LlvmOpcode.FPToSI && LlvmKnownFPClasses.Compute(cast.Operand, LlvmFPClassTest.NegNormal, context, tree, metrics, (int)depth + 1, assumptions, allowEphemerals, conditions, condContext).IsKnownNever(LlvmFPClassTest.NegNormal))
                    return new LlvmKnownBits(width).WithNonNegative();
                return new(width);
            case LlvmGetElementPtrInstruction gep:
                return ComputeGep(gep, gep.SourceElementType, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout, demand);
            case LlvmSelectInstruction select:
                Count(metrics, "known_bits.select");
                return AdjustSelectArm(query.Recurse(select.TrueValue), select.Condition, select.TrueValue, false, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout)
                    .IntersectWith(AdjustSelectArm(query.Recurse(select.FalseValue), select.Condition, select.FalseValue, true, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
            case LlvmPhiInstruction phi:
                Count(metrics, "known_bits.phi");
                return ComputePhi(phi, depth, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout, demand);
            case LlvmFreezeInstruction freeze:
                return LlvmValueTracking.IsGuaranteedNotToBePoison(freeze.Operand, tree, context, depth + 1, metrics, assumptions, allowEphemerals, conditions) ? query.Recurse(freeze.Operand) : new(width);
            case LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } reverse } reverseCall when reverse.Kind == LlvmIntrinsicKind.VectorReverse:
                Count(metrics, "known_bits.vector.reverse");
                return query.Recurse(reverseCall.GetArgument(0), demand.ReverseBits());
            case LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } intrinsic } call when IsMinMaxIntrinsicKind(intrinsic.Kind):
                return ComputeMinMaxIntrinsic(call, intrinsic.Kind, query);
            case LlvmHeapAllocInst heap:
                Count(metrics, "known_bits.heap_alignment");
                return new(LlvmAPInt.GetLowBitsSet(width, (uint)BitOperations.TrailingZeroCount(heap.Alignment.Value)), LlvmAPInt.GetZero(width));
            case LlvmAllocaInstruction alloca:
                return new(LlvmAPInt.GetLowBitsSet(width, (uint)BitOperations.TrailingZeroCount(alloca.Alignment.Value)), LlvmAPInt.GetZero(width));
            default:
                // Pointer alignment is refined by the shared outer source path.
                Count(metrics, "known_bits.no_source_facts");
                return new(width);
        }
    }

    private static LlvmKnownBits ComputeBinary(LlvmBinaryOperator binary, BitsQuery query, uint depth, LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics)
    {
        bool nsw = query.UseInstructionInfo && binary.HasNoSignedWrap, nuw = query.UseInstructionInfo && binary.HasNoUnsignedWrap, exact = query.UseInstructionInfo && binary.IsExact;
        LlvmKnownBits right = query.Recurse(binary.Right);
        if (binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub && right.IsUnknown && !nsw && !nuw)
            return binary.Opcode == LlvmOpcode.Add ? RefineAdd(binary.Left, binary.Right, new(right.BitWidth), query, depth, context, tree, metrics, lerp: false) : new(right.BitWidth);
        LlvmKnownBits left = query.Recurse(binary.Left);
        LlvmKnownBits result = binary.Opcode switch
        {
            LlvmOpcode.And => left & right,
            LlvmOpcode.Or => left | right,
            LlvmOpcode.Xor => left ^ right,
            LlvmOpcode.Add or LlvmOpcode.Sub => LlvmKnownBits.AddSub(binary.Opcode == LlvmOpcode.Add, nsw, nuw, left, right),
            LlvmOpcode.Mul => LlvmKnownBits.Mul(left, right, ReferenceEquals(binary.Left, binary.Right) && LlvmValueTracking.IsGuaranteedNotToBeUndef(binary.Left, tree, context, metrics, depth + 1, query.Assumptions, query.AllowEphemerals, query.Conditions)),
            LlvmOpcode.UDiv => LlvmKnownBits.UDiv(left, right, exact),
            LlvmOpcode.SDiv => LlvmKnownBits.SDiv(left, right, exact),
            LlvmOpcode.URem => LlvmKnownBits.URem(left, right),
            LlvmOpcode.SRem => LlvmKnownBits.SRem(left, right),
            LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr => LlvmKnownBits.Shift(binary.Opcode, left, right, nuw, nsw, exact,
                right.IsNonZero || (right.GetMaxValue().Ult(new(right.BitWidth, right.BitWidth)) && IsKnownNonZero(binary.Right, depth + 1, context, tree, metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.Conditions, query.CondContext))),
            _ => throw new InvalidOperationException("Floating arithmetic does not produce integer known bits.")
        };
        if (binary.Opcode == LlvmOpcode.Sub && nsw && !result.IsNonNegative && context?.Parent?.Parent?.Parent is LlvmModule module
            && LlvmInstructionSimplify.IsImpliedByDomCondition(LlvmCmpPredicate.ICmpSle, binary.Right, binary.Left,
                new(module.DataLayout, context, metrics, tree, query.UseInstructionInfo,
                    Assumptions: query.Assumptions, AllowEphemerals: query.AllowEphemerals,
                    DC: query.Conditions, CondContext: query.CondContext)) == true)
            result = result.WithNonNegative();
        if (binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)
            result = RefineBitwise(binary, left, right, result, query);
        if (binary.Opcode == LlvmOpcode.Add) result = RefineAdd(binary.Left, binary.Right, result, query, depth, context, tree, metrics);
        if (binary.Opcode == LlvmOpcode.Mul && nsw)
        {
            bool nonnegative = ReferenceEquals(binary.Left, binary.Right) || (left.IsNegative && right.IsNegative) || (left.IsNonNegative && right.IsNonNegative);
            if (!nonnegative && nuw)
                nonnegative = left.GetSignedMinValue().Sgt(new(left.BitWidth, 1)) || right.GetSignedMinValue().Sgt(new(left.BitWidth, 1));
            if (nonnegative && !result.IsNegative) result = result.WithNonNegative();
            else if (!result.IsNonNegative && ((left.IsNegative && right.IsNonNegative && right.IsNonZero) || (right.IsNegative && left.IsNonNegative && left.IsNonZero))) result = result.WithNegative();
        }
        if (binary.Opcode == LlvmOpcode.Mul && ReferenceEquals(binary.Left, binary.Right)
            && LlvmValueTracking.IsGuaranteedNotToBeUndef(binary.Left, tree, context, metrics, depth + 1, query.Assumptions, query.AllowEphemerals, query.Conditions))
        {
            uint validBits = 2 * (left.BitWidth - ComputeNumSignBits(binary.Left, depth + 1, context, tree, metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.Conditions, query.CondContext) + 1);
            if (validBits < left.BitWidth) result = new(result.Zero | LlvmAPInt.GetHighBitsSet(left.BitWidth, left.BitWidth - validBits + 1), result.One);
        }
        if (binary.Opcode == LlvmOpcode.Shl && ReferenceEquals(binary.Left, binary.Right)
            && LlvmValueTracking.IsGuaranteedNotToBeUndef(binary.Left, tree, context, metrics, depth + 1, query.Assumptions, query.AllowEphemerals, query.Conditions))
            result = new(result.Zero | new LlvmAPInt(left.BitWidth, 1), result.One);
        if (binary.Left is LlvmConstantInt constant)
        {
            if (binary.Opcode == LlvmOpcode.Shl) result = new(result.Zero | LlvmAPInt.GetLowBitsSet(result.BitWidth, constant.Value.CountTrailingZeros()), result.One);
            if (binary.Opcode == LlvmOpcode.LShr) result = new(result.Zero | LlvmAPInt.GetHighBitsSet(result.BitWidth, constant.Value.CountLeadingZeros()), result.One);
        }
        Count(metrics, result.IsUnknown ? "known_bits.binary.unknown" : "known_bits.binary.facts");
        return result;
    }

    public static bool? Compare(LlvmCmpPredicate predicate, LlvmKnownBits left, LlvmKnownBits right)
    {
        if (predicate == LlvmCmpPredicate.ICmpEq)
        {
            if (left.IsConstant && right.IsConstant) return left.One == right.One;
            if (!(left.One & right.Zero).IsZero || !(right.One & left.Zero).IsZero) return false;
            return null;
        }
        if (predicate == LlvmCmpPredicate.ICmpNe) return Negate(Compare(LlvmCmpPredicate.ICmpEq, left, right));
        if (predicate == LlvmCmpPredicate.ICmpUlt) return Compare(LlvmCmpPredicate.ICmpUgt, right, left);
        if (predicate == LlvmCmpPredicate.ICmpSlt) return Compare(LlvmCmpPredicate.ICmpSgt, right, left);
        if (predicate == LlvmCmpPredicate.ICmpUge) return Negate(Compare(LlvmCmpPredicate.ICmpUgt, right, left));
        if (predicate == LlvmCmpPredicate.ICmpSge) return Negate(Compare(LlvmCmpPredicate.ICmpSgt, right, left));
        if (predicate == LlvmCmpPredicate.ICmpUle) return Negate(Compare(LlvmCmpPredicate.ICmpUgt, left, right));
        if (predicate == LlvmCmpPredicate.ICmpSle) return Negate(Compare(LlvmCmpPredicate.ICmpSgt, left, right));
        if (predicate == LlvmCmpPredicate.ICmpUgt)
        {
            if (left.GetMaxValue().Ule(right.GetMinValue())) return false;
            if (left.GetMinValue().Ugt(right.GetMaxValue())) return true;
        }
        if (predicate == LlvmCmpPredicate.ICmpSgt)
        {
            if (left.GetSignedMaxValue().Sle(right.GetSignedMinValue())) return false;
            if (left.GetSignedMinValue().Sgt(right.GetSignedMaxValue())) return true;
        }
        return null;
    }
    private static bool? Negate(bool? value) => value.HasValue ? !value.Value : null;
    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
