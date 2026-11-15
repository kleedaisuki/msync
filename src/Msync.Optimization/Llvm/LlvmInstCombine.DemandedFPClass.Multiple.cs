// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSimplifyDemanded.cpp
// simplifyDemandedFPClassMinMax / SimplifyMultipleUseDemandedFPClass.
using System.Numerics;
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool FPCannotOrderGreater(C left, C right, bool orderedZero)
        {
            uint lhs = (uint)(left & ~C.NaN), rhs = (uint)(right & ~C.NaN);
            if (lhs == 0 || rhs == 0) return true;
            uint low = rhs & unchecked(0U - rhs), high = 1U << BitOperations.Log2(lhs);
            if (!orderedZero)
            {
                if (low == (uint)C.NegZero) low = (uint)C.PosZero;
                if (high == (uint)C.NegZero) high = (uint)C.PosZero;
            }
            return low > high || low == high && (low & (uint)(C.Zero | C.Infinity)) != 0;
        }
        private LlvmValue? DemandedFPMinMax(LlvmCallInstruction call, C demanded,
            ref LlvmKnownFPClass known, LlvmKnownFPClass left, LlvmKnownFPClass right, bool noSignedZeros)
        {
            var kind = call.CalledFunction!.IntrinsicDescriptor!.Kind;
            bool minimum = kind == LlvmIntrinsicKind.MinNum || kind == LlvmIntrinsicKind.Minimum || kind == LlvmIntrinsicKind.MinimumNum;
            bool propagateNaN = kind == LlvmIntrinsicKind.Minimum || kind == LlvmIntrinsicKind.Maximum;
            bool orderedZero = !noSignedZeros;
            bool leftOrdered = minimum ? FPCannotOrderGreater(left.GetKnownFPClasses(), right.GetKnownFPClasses(), orderedZero)
                : FPCannotOrderGreater(right.GetKnownFPClasses(), left.GetKnownFPClasses(), orderedZero);
            bool rightOrdered = minimum ? FPCannotOrderGreater(right.GetKnownFPClasses(), left.GetKnownFPClasses(), orderedZero)
                : FPCannotOrderGreater(left.GetKnownFPClasses(), right.GetKnownFPClasses(), orderedZero);
            if (leftOrdered && (propagateNaN ? right : left).IsKnownNeverNaN()) return call.GetArgument(0);
            if (rightOrdered && (propagateNaN ? left : right).IsKnownNeverNaN()) return call.GetArgument(1);
            var operation = kind == LlvmIntrinsicKind.MinNum ? LlvmFPMinMaxKind.MinNum :
                kind == LlvmIntrinsicKind.MaxNum ? LlvmFPMinMaxKind.MaxNum :
                kind == LlvmIntrinsicKind.Minimum ? LlvmFPMinMaxKind.Minimum :
                kind == LlvmIntrinsicKind.Maximum ? LlvmFPMinMaxKind.Maximum :
                kind == LlvmIntrinsicKind.MinimumNum ? LlvmFPMinMaxKind.MinimumNum : LlvmFPMinMaxKind.MaximumNum;
            known = LlvmKnownFPClass.MinMaxLike(left, right, operation, F.GetDenormalMode(call.Type.GetScalarType().GetFltSemantics()));
            known.KnownNot(~demanded);
            return FPClassConstant(call.Type, known.GetKnownFPClasses(), true);
        }
        private LlvmValue? SimplifyMultipleUseDemandedFPClass(LlvmInstruction instruction, C demanded,
            ref LlvmKnownFPClass known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            var flags = instruction is ILlvmFastMathInstruction floating ? floating.FastMathFlags : default;
            demanded = AdjustFPDemand(demanded, flags);
            if (instruction is LlvmSelectInstruction select)
            {
                var right = ComputeDemandedFP(select.FalseValue, demanded, query, depth + 1);
                if (right.IsKnownNever(demanded)) return select.TrueValue;
                var left = ComputeDemandedFP(select.TrueValue, demanded, query, depth + 1);
                if (left.IsKnownNever(demanded)) return select.FalseValue;
                left = AdjustDemandedFPSelectArm(left, select, false, query, depth);
                right = AdjustDemandedFPSelectArm(right, select, true, query, depth);
                known = left.IntersectWith(right);
                known.KnownNot(~demanded);
            }
            else if (instruction is LlvmUnaryOperator negate && LlvmKnownFPClasses.LookThroughFAbs(negate.Operand, out var magnitude))
            {
                var source = ComputeDemandedFP(magnitude, C.All, query, depth + 1);
                var innerFlags = ((LlvmCallInstruction)negate.Operand).FastMathFlags;
                var replacement = DemandedFAbs(ref known, magnitude, AdjustFPDemand(demanded, innerFlags), source, false, true);
                if (replacement is not null) return replacement;
            }
            else if (instruction is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor)
            {
                var kind = descriptor.Kind;
                if (kind == LlvmIntrinsicKind.FAbs)
                {
                    var source = ComputeDemandedFP(call.GetArgument(0), C.All, query, depth + 1);
                    if (DemandedFAbs(ref known, call.GetArgument(0), demanded, source, false) is { } replacement) return replacement;
                }
                else if (kind == LlvmIntrinsicKind.CopySign)
                {
                    var magnitudeKnown = ComputeDemandedFP(call.GetArgument(0), C.All, query, depth + 1);
                    magnitudeKnown.KnownNot(~UnknownSignFPDemand(demanded));
                    if (DemandedCopySignMagnitude(call.GetArgument(0), demanded, magnitudeKnown, false) is { } replacement) return replacement;
                    var signKnown = ComputeDemandedFP(call.GetArgument(1), C.All, query, depth + 1);
                    signKnown.KnownNot((flags & LlvmFastMathFlags.NoInfs) != 0 ? C.Infinity : C.None);
                    signKnown.KnownNot((flags & LlvmFastMathFlags.NoNaNs) != 0 ? C.NaN : C.None);
                    if (signKnown.SignBit is bool sign && magnitudeKnown.SignBit == sign) return call.GetArgument(0);
                    known = LlvmKnownFPClass.CopySign(magnitudeKnown, signKnown);
                }
                else if (LlvmIntrinsic.IsFloatingMinMax(kind))
                {
                    var right = ComputeDemandedFP(call.GetArgument(1), demanded, query, depth + 1);
                    if (right.IsUnknown) return null;
                    return DemandedFPMinMax(call, demanded, ref known,
                        ComputeDemandedFP(call.GetArgument(0), demanded, query, depth + 1), right, false);
                }
                else known = ComputeDemandedFP(instruction, demanded, query, depth + 1);
            }
            else known = ComputeDemandedFP(instruction, demanded, query, depth + 1);
            known.KnownNot(~demanded);
            return FPClassConstant(instruction.Type, known.GetKnownFPClasses());
        }
        private LlvmKnownFPClass AdjustDemandedFPSelectArm(LlvmKnownFPClass known, LlvmSelectInstruction select,
            bool invert, LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmKnownFPClasses.AdjustForSelectArm(known, invert ? select.FalseValue : select.TrueValue,
                select.Condition, invert, query.Context, query.Tree, query.Metrics, (int)depth,
                query.Assumptions, query.AllowEphemerals, query.DC, query.CondContext);
    }
}
