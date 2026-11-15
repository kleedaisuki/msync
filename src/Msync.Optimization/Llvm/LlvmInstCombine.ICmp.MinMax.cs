// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldICmpWithMinMax/foldICmpWithClamp (6022–6215), scalar intrinsic representation.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmCmpPredicate ICmpStrict(LlvmCmpPredicate predicate) => predicate switch
        {
            LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUlt, LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
            LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt, LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt, _ => predicate
        };
        private static LlvmCmpPredicate ICmpNonStrict(LlvmCmpPredicate predicate) => predicate switch
        {
            LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUle, LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpUge,
            LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSle, LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSge, _ => predicate
        };
        private LlvmInstruction? FoldICmpWithMinMax(LlvmICmpInstruction instruction, LlvmCallInstruction minMax, LlvmValue z, LlvmCmpPredicate predicate)
        {
            Count("icmp.minmax.attempts");
            var kind = minMax.CalledFunction!.IntrinsicDescriptor!.Kind;
            bool signed = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax;
            if (ICmpIsSigned(predicate) && !signed) return null;
            if (ICmpIsUnsigned(predicate) && signed)
            {
                if (!IsKnownNonNegative(z, instruction) || !IsKnownNonNegative(minMax, instruction)) return null;
                predicate = ICmpSignedPredicate(predicate);
                Count("icmp.minmax.signedness_reverted");
            }
            var x = minMax.GetArgument(0); var y = minMax.GetArgument(1);
            var query = Query(instruction);
            bool? Known(LlvmCmpPredicate p, LlvmValue a, LlvmValue b)
            {
                Count("icmp.minmax.simplify_queries");
                bool? known = LlvmInstructionSimplify.SimplifyICmp(p, a, b, query) is LlvmConstantInt c ? c.IsOne ? true : c.IsZero ? false : null : null;
                Count(known.HasValue ? known.Value ? "icmp.minmax.proven_true" : "icmp.minmax.proven_false" : "icmp.minmax.unproven");
                return known;
            }
            bool? xz = Known(predicate, x, z), yz = Known(predicate, y, z);
            if (!xz.HasValue && !yz.HasValue) return null;
            if (!xz.HasValue) { (x, y) = (y, x); (xz, yz) = (yz, xz); }
            LlvmInstruction? Constant(bool value)
            {
                Count("icmp.minmax.constant");
                return ReplaceInstUsesWith(instruction, LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, value ? 1UL : 0UL));
            }
            // Newly speculated comparisons intentionally do not inherit samesign.
            LlvmInstruction? CompareYZ()
            {
                if (yz.HasValue) return Constant(yz.Value);
                Count("icmp.minmax.reduce_operand");
                return new LlvmICmpInstruction(predicate, y, z);
            }
            var minMaxPredicate = LlvmIntrinsic.GetMinMaxPredicate(kind);
            if (!ICmpIsEquality(predicate))
            {
                bool same = minMaxPredicate == ICmpStrict(predicate);
                return xz!.Value == same ? Constant(xz.Value) : CompareYZ();
            }
            if ((predicate == LlvmCmpPredicate.ICmpEq) == xz!.Value)
            {
                var resultPredicate = ICmpNonStrict(minMaxPredicate);
                if (predicate == LlvmCmpPredicate.ICmpNe) resultPredicate = LlvmCmpInstruction.GetInversePredicate(resultPredicate);
                Count("icmp.minmax.equal_bound");
                return new LlvmICmpInstruction(resultPredicate, x, y);
            }
            bool? bound = Known(minMaxPredicate, x, z);
            if (!bound.HasValue)
            {
                (x, y) = (y, x); (xz, yz) = (yz, xz);
                if (!xz.HasValue || (predicate == LlvmCmpPredicate.ICmpEq) == xz.Value) return null;
                bound = Known(minMaxPredicate, x, z);
            }
            return !bound.HasValue ? null : bound.Value ? Constant(predicate == LlvmCmpPredicate.ICmpNe) : CompareYZ();
        }

        private LlvmInstruction? FoldICmpWithClamp(LlvmICmpInstruction instruction, LlvmValue x, LlvmCallInstruction min)
        {
            Count("icmp.clamp.attempts");
            var kind = min.CalledFunction!.IntrinsicDescriptor!.Kind;
            if (!ICmpIsEquality(instruction.Predicate) || !min.HasOneUse || (kind != LlvmIntrinsicKind.SMin && kind != LlvmIntrinsicKind.UMin)) return null;
            var maxKind = kind == LlvmIntrinsicKind.SMin ? LlvmIntrinsicKind.SMax : LlvmIntrinsicKind.UMax;
            if (!CallsIntrinsic(min.GetArgument(0), maxKind, out var max) || !max.HasOneUse || max.GetArgument(0) != x ||
                max.GetArgument(1) is not LlvmConstantInt low || min.GetArgument(1) is not LlvmConstantInt high ||
                !(kind == LlvmIntrinsicKind.SMin ? low.Value.Slt(high.Value) : low.Value.Ult(high.Value))) return null;
            var range = LlvmConstantRange.GetNonEmpty(low.Value, high.Value + new LlvmAPInt(high.Value.BitWidth, 1));
            if (instruction.Predicate == LlvmCmpPredicate.ICmpNe) range = range.Inverse();
            range.GetEquivalentICmp(out var predicate, out var constant, out var offset);
            if (!offset.IsZero) x = Builder.CreateAdd(x, LlvmConstantInt.Get(x.Context, offset));
            Count("icmp.clamp.range_rewrite");
            return ReplaceInstUsesWith(instruction, Builder.CreateICmp(predicate, x, LlvmConstantInt.Get(x.Context, constant)));
        }
    }
}
