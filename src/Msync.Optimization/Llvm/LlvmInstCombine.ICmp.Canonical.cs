// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// canonicalizeCmpWithConstant/canonicalizeICmpPredicate/canonicalizeICmpBool;
// ValueTracking.cpp getFlippedStrictnessPredicateAndConstant scalar/vector constants.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmICmpInstruction? CanonicalizeCmpWithConstant(LlvmICmpInstruction compare)
        {
            var predicate = compare.Predicate;
            if (ICmpIsEquality(predicate) || IsCanonicalPredicate(predicate) || compare.Right is not LlvmConstant constant) return null;
            var flipped = LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, constant, compare.SameSign, Metrics);
            if (flipped is null) return null;
            Count("icmp.canonical.strictness");
            return new(flipped.Value.Predicate, compare.Left, flipped.Value.Constant) { SameSign = compare.SameSign };
        }

        private LlvmCmpInstruction? CanonicalizeICmpPredicate(LlvmCmpInstruction compare)
        {
            if (IsCanonicalPredicate(compare.Predicate) || !CanFreelyInvertAllUsersOf(compare, null)) return null;
            compare.Predicate = LlvmCmpInstruction.GetInversePredicate(compare.Predicate);
            compare.SetName(compare.Name + ".not");
            FreelyInvertAllUsersOf(compare, null);
            Count("icmp.canonical.invert_users");
            return compare;
        }

        private LlvmInstruction CanonicalizeICmpBool(LlvmICmpInstruction compare)
        {
            var a = compare.Left; var b = compare.Right;
            bool zero = ICmpConstantMatches(b, static bits => bits.IsZero);
            bool one = ICmpConstantMatches(b, static bits => bits.IsOne);
            if (zero || one)
            {
                bool invert = zero
                    ? compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge
                    : compare.Predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSgt;
                if (invert) return ICmpDetachedNot(a);
                throw new InvalidOperationException("Constant boolean comparison was not simplified before canonicalization.");
            }
            return compare.Predicate switch
            {
                LlvmCmpPredicate.ICmpEq => ICmpDetachedNot(Builder.CreateXor(a, b)),
                LlvmCmpPredicate.ICmpNe => new LlvmBinaryOperator(LlvmOpcode.Xor, a, b),
                LlvmCmpPredicate.ICmpUgt => new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(b), a),
                LlvmCmpPredicate.ICmpUlt => new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(a), b),
                LlvmCmpPredicate.ICmpSgt => new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(a), b),
                LlvmCmpPredicate.ICmpSlt => new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(b), a),
                LlvmCmpPredicate.ICmpUge => new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateNot(b), a),
                LlvmCmpPredicate.ICmpUle => new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateNot(a), b),
                LlvmCmpPredicate.ICmpSge => new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateNot(a), b),
                LlvmCmpPredicate.ICmpSle => new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateNot(b), a),
                _ => throw new InvalidOperationException("Invalid integer predicate.")
            };
        }

        private static LlvmBinaryOperator ICmpDetachedNot(LlvmValue value) =>
            new(LlvmOpcode.Xor, value, LlvmConstant.GetAllOnesValue(value.Type));
    }
}
