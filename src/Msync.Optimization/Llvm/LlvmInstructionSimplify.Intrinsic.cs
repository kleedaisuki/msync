// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// simplifyIntrinsic, simplifyBinaryIntrinsic minmax and foldMinMaxSharedOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifyIntrinsic(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) => SimplifyScalarIntrinsic(kind, left.Type, [left, right], new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals));

    public static LlvmValue? SimplifyIntrinsic(LlvmIntrinsicKind kind, LlvmType resultType, IReadOnlyList<LlvmValue> arguments,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) => SimplifyScalarIntrinsic(kind, resultType, arguments, new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals));

    private static LlvmValue? SimplifyMinMaxIntrinsic(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.intrinsic.minmax.queries");
        if (left is LlvmPoisonValue || right is LlvmPoisonValue)
        { Count(q.Metrics, "simplify.intrinsic.minmax.poison"); return LlvmPoisonValue.Get(left.Type); }
        if (left is LlvmConstant lc && right is LlvmConstant rc)
            if (LlvmConstantFolding.ConstantFoldIntrinsic(kind, [lc, rc], left.Type, q.Metrics) is { } folded)
            { Count(q.Metrics, "simplify.intrinsic.minmax.constant_fold"); return folded; }
        if (left == right) { Count(q.Metrics, "simplify.intrinsic.minmax.equal_operands"); return left; }
        if (left is LlvmConstant) { (left, right) = (right, left); Count(q.Metrics, "simplify.intrinsic.minmax.commuted"); }
        uint width = left.Type.GetIntegerBitWidth();
        if (q.IsUndef(right))
        { Count(q.Metrics, "simplify.intrinsic.minmax.undef_saturation"); return LlvmConstantInt.Get(left.Context, LlvmIntrinsic.GetSaturationPoint(kind, width)); }
        if (right is LlvmConstantInt constant)
        {
            if (constant.Value == LlvmIntrinsic.GetSaturationPoint(kind, width))
            { Count(q.Metrics, "simplify.intrinsic.minmax.saturation_operand"); return constant; }
            if (constant.Value == LlvmIntrinsic.GetSaturationPoint(LlvmIntrinsic.GetInverseMinMax(kind), width))
            { Count(q.Metrics, "simplify.intrinsic.minmax.identity_operand"); return left; }
            if (left is LlvmCallBase inner && inner.CalledFunction is { } target && LlvmIntrinsic.GetDescriptor(target)?.Kind == kind)
            {
                LlvmConstantInt? innerConstant = inner.GetArgument(0) as LlvmConstantInt ?? inner.GetArgument(1) as LlvmConstantInt;
                if (innerConstant is not null && MinMaxNonStrictCompare(kind, innerConstant.Value, constant.Value))
                { Count(q.Metrics, "simplify.intrinsic.minmax.nested_constant"); return left; }
            }
        }
        if (FoldMinMaxSharedOp(kind, left, right) is { } sharedLeft)
        { Count(q.Metrics, "simplify.intrinsic.minmax.shared_operand"); return sharedLeft; }
        if (FoldMinMaxSharedOp(kind, right, left) is { } sharedRight)
        { Count(q.Metrics, "simplify.intrinsic.minmax.shared_operand_commuted"); return sharedRight; }
        LlvmCmpPredicate predicate = (LlvmCmpPredicate)((byte)LlvmIntrinsic.GetMinMaxPredicate(kind) + 1);
        BinaryQuery withoutUndef = q with { CanUseUndef = false };
        if (IsIntegerCmpTrue(predicate, left, right, withoutUndef, 3))
        { Count(q.Metrics, "simplify.intrinsic.minmax.compare_left"); return left; }
        if (IsIntegerCmpTrue(predicate, right, left, withoutUndef, 3))
        { Count(q.Metrics, "simplify.intrinsic.minmax.compare_right"); return right; }
        Count(q.Metrics, "simplify.intrinsic.minmax.no_fold");
        return null;
    }

    private static bool MinMaxNonStrictCompare(LlvmIntrinsicKind kind, LlvmAPInt left, LlvmAPInt right) =>
        LlvmIntrinsic.GetMinMaxPredicate(kind) switch
        {
            LlvmCmpPredicate.ICmpSlt => left.Sle(right), LlvmCmpPredicate.ICmpSgt => left.Sge(right),
            LlvmCmpPredicate.ICmpUlt => left.Ule(right), _ => left.Uge(right)
        };

    private static LlvmValue? FoldMinMaxSharedOp(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right)
    {
        if (left is not LlvmCallBase inner || inner.CalledFunction is not { } function ||
            LlvmIntrinsic.GetDescriptor(function) is not { } descriptor || !LlvmIntrinsic.IsMinMax(descriptor.Kind)) return null;
        LlvmValue x = inner.GetArgument(0), y = inner.GetArgument(1);
        bool shared = right == x || right == y;
        if (!shared && right is LlvmCallBase other && other.CalledFunction is { } otherFunction &&
            LlvmIntrinsic.GetDescriptor(otherFunction) is { } otherDescriptor && LlvmIntrinsic.IsMinMax(otherDescriptor.Kind))
            shared = other.GetArgument(0) == x && other.GetArgument(1) == y || other.GetArgument(0) == y && other.GetArgument(1) == x;
        if (!shared) return null;
        if (descriptor.Kind == kind) return inner;
        return descriptor.Kind == LlvmIntrinsic.GetInverseMinMax(kind) ? right : null;
    }
}
