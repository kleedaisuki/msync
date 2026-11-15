// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/PatternMatch.h UAddWithOverflow_match.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool ICmpMatchUAddWithOverflow(LlvmICmpInstruction compare, out LlvmValue left,
            out LlvmValue right, out LlvmInstruction operation)
        {
            var a = compare.Left; var b = compare.Right;
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt && a is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add &&
                (b == add.Left || b == add.Right))
            { left = add.Left; right = add.Right; operation = add; return true; }
            if (compare.Predicate == LlvmCmpPredicate.ICmpUgt && b is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } reversed &&
                (a == reversed.Left || a == reversed.Right))
            { left = reversed.Left; right = reversed.Right; operation = reversed; return true; }
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt && a.HasOneUse && ICmpMatchNot(a) is { } invertedA)
            { left = invertedA; right = b; operation = (LlvmInstruction)a; return true; }
            if (compare.Predicate == LlvmCmpPredicate.ICmpUgt && b.HasOneUse && ICmpMatchNot(b) is { } invertedB)
            { left = invertedB; right = a; operation = (LlvmInstruction)b; return true; }
            if (compare.Predicate == LlvmCmpPredicate.ICmpEq)
            {
                if (a is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } increment && b is LlvmConstantInt { IsZero: true } &&
                    (increment.Left is LlvmConstantInt { IsOne: true } || increment.Right is LlvmConstantInt { IsOne: true }))
                { left = increment.Left; right = increment.Right; operation = increment; return true; }
                if (b is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } incrementRight && a is LlvmConstantInt { IsZero: true } &&
                    (incrementRight.Left is LlvmConstantInt { IsOne: true } || incrementRight.Right is LlvmConstantInt { IsOne: true }))
                { left = incrementRight.Left; right = incrementRight.Right; operation = incrementRight; return true; }
            }
            left = right = null!; operation = null!;
            return false;
        }
    }
}
