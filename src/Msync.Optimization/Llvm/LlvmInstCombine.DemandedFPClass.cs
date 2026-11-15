// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp getFPClassConstant, demanded FPClass entry points.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private const uint DemandedFPMaxDepth = 6;
        private LlvmKnownFPClass ComputeDemandedFP(LlvmValue value, C demanded,
            LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmKnownFPClasses.Compute(value, demanded, query.Context, query.Tree, query.Metrics, (int)depth,
                query.Assumptions, query.AllowEphemerals, query.DC, query.CondContext);

        private static LlvmConstant? FPClassConstant(LlvmType type, C classes, bool canonicalizing = false)
        {
            if (classes == C.None) return LlvmPoisonValue.Get(type);
            if (classes == C.PosZero) return LlvmConstant.GetNullValue(type);
            if (type.IsAggregateType()) return null;
            var semantics = type.GetScalarType().GetFltSemantics();
            LlvmAPFloat? value = classes switch
            {
                C.NegZero => LlvmAPFloat.GetZero(semantics, true),
                C.PosInfinity => LlvmAPFloat.GetInf(semantics),
                C.NegInfinity => LlvmAPFloat.GetInf(semantics, true),
                C.QNaN or C.NaN when canonicalizing => LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(semantics).Value,
                _ => null
            };
            return value is null ? null : LlvmConstantFP.Get(type, value.Value);
        }
        private static C AdjustFPDemand(C demanded, LlvmFastMathFlags flags)
        {
            if ((flags & LlvmFastMathFlags.NoNaNs) != 0) demanded &= ~C.NaN;
            if ((flags & LlvmFastMathFlags.NoInfs) != 0) demanded &= ~C.Infinity;
            return demanded;
        }
        private static C NegateFPDemand(C classes) => LlvmKnownFPClass.Negate(new(classes)).GetKnownFPClasses();
        private static C UnknownSignFPDemand(C classes)
        {
            var known = new LlvmKnownFPClass(classes);
            known.SetSignBit(null);
            return known.GetKnownFPClasses();
        }
        private static LlvmFastMathFlags InferFPValueFlags(LlvmFastMathFlags flags, C valid,
            params LlvmKnownFPClass[] operands)
        {
            if ((valid & C.NaN) == 0 && operands.All(value => value.IsKnownNeverNaN())) flags |= LlvmFastMathFlags.NoNaNs;
            if ((valid & C.Infinity) == 0 && operands.All(value => value.IsKnownNeverInfinity())) flags |= LlvmFastMathFlags.NoInfs;
            return flags;
        }
        private LlvmValue? DemandedFPResult(LlvmInstruction instruction, LlvmFastMathFlags flags,
            C demanded, LlvmKnownFPClass known, params LlvmKnownFPClass[] operands)
        {
            C valid = demanded & known.GetKnownFPClasses();
            if (FPClassConstant(instruction.Type, valid, true) is { } constant) return constant;
            var inferred = InferFPValueFlags(flags, valid, operands);
            if (inferred == flags || instruction is not ILlvmFastMathInstruction floating) return null;
            instruction.DropUBImplyingAttrsAndMetadata();
            floating.FastMathFlags = inferred;
            Count("demanded_fp.flags_inferred");
            return instruction;
        }
        internal bool SimplifyDemandedInstructionFPClass(LlvmInstruction instruction)
        {
            Count("demanded_fp.instruction_queries");
            var known = new LlvmKnownFPClass();
            var value = SimplifyDemandedUseFPClass(instruction, C.All, ref known, Query(instruction));
            if (value is null) return false;
            if (value != instruction) ReplaceInstUsesWith(instruction, value);
            Count("demanded_fp.instruction_rewrites");
            return true;
        }
        private bool SimplifyDemandedFPClass(LlvmInstruction instruction, int operand, C demanded,
            out LlvmKnownFPClass known, LlvmInstructionSimplify.BinaryQuery query, uint depth = 0)
        {
            Count("demanded_fp.operand_queries");
            demanded &= C.All;
            known = new();
            var use = instruction.GetOperandUse(operand);
            var value = use.Value!;
            if (demanded == C.None)
            {
                if (value is LlvmPoisonValue) return false;
                ReplaceUse(use, LlvmPoisonValue.Get(value.Type));
                Count("demanded_fp.undemanded_operand");
                return true;
            }
            if (value is not LlvmInstruction source)
            {
                known = ComputeDemandedFP(value, C.All, query, depth);
                known.KnownNot(~demanded);
                if (known.GetKnownFPClasses() == C.None)
                {
                    if (value is LlvmPoisonValue) return false;
                    ReplaceUse(use, LlvmPoisonValue.Get(value.Type));
                    Count("demanded_fp.excluded_operand");
                    return true;
                }
                if (value is LlvmConstant) return false;
                var constant = FPClassConstant(value.Type, known.GetKnownFPClasses());
                if (constant is null || constant == value) return false;
                ReplaceUse(use, constant);
                Count("demanded_fp.constant_operand");
                return true;
            }
            if (depth == DemandedFPMaxDepth)
            { known.KnownNot(~demanded); Count("demanded_fp.depth_limit"); return false; }
            var replacement = source.HasOneUse
                ? SimplifyDemandedUseFPClass(source, demanded, ref known, query, depth)
                : SimplifyMultipleUseDemandedFPClass(source, demanded, ref known, query, depth);
            if (replacement is null) return false;
            ReplaceUse(use, replacement);
            Count("demanded_fp.operand_rewrites");
            return true;
        }
        private static LlvmValue? DemandedFAbs(ref LlvmKnownFPClass known, LlvmValue source,
            C demanded, LlvmKnownFPClass sourceKnown, bool noSignedZeros, bool negate = false)
        {
            if ((demanded & C.NaN) == 0) sourceKnown.KnownNot(C.NaN);
            if ((demanded & C.Infinity) == 0) sourceKnown.KnownNot(C.Infinity);
            if (sourceKnown.SignBit == negate || !negate && (demanded & C.NaN) == 0 && sourceKnown.IsKnownNever(C.Negative)) return source;
            C opposite = negate ? LlvmKnownFPClass.OrderedGreaterThanZeroMask : LlvmKnownFPClass.OrderedLessThanZeroMask;
            if (noSignedZeros && sourceKnown.IsKnownNever(opposite | C.NaN)) return source;
            known = LlvmKnownFPClass.FAbs(sourceKnown);
            if (negate) known.Negate();
            known.KnownNot(~demanded);
            return null;
        }
        private static LlvmValue? DemandedCopySignMagnitude(LlvmValue magnitude, C demanded,
            LlvmKnownFPClass known, bool noSignedZeros)
        {
            if (noSignedZeros)
            {
                C negative = C.Negative | C.PosZero, positive = C.Positive | C.NegZero;
                if ((demanded & ~negative) == 0 && known.IsKnownAlways(negative) ||
                    (demanded & ~positive) == 0 && known.IsKnownAlways(positive)) return magnitude;
            }
            else if ((demanded & ~C.Negative) == 0 && known.SignBit == true ||
                (demanded & ~C.Positive) == 0 && known.SignBit == false) return magnitude;
            return null;
        }
    }
}
