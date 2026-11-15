// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldICmpOfUAddOv/foldICmpInvariantGroup (represented scalar intrinsic IDs).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpOfUAddOv(LlvmICmpInstruction compare)
        {
            LlvmCallBase? sum = null;
            if (compare.Left is LlvmExtractValueInstruction { Indices: [0] } left && CallsIntrinsic(left.AggregateOperand,
                LlvmIntrinsicKind.UAddWithOverflow, out var lcall))
            {
                var a = lcall.GetArgument(0); var b = lcall.GetArgument(1);
                if (compare.Predicate == LlvmCmpPredicate.ICmpUlt && (compare.Right == a || compare.Right == b) ||
                    compare.Predicate == LlvmCmpPredicate.ICmpEq && compare.Right is LlvmConstantInt { IsZero: true } &&
                        (a is LlvmConstantInt { IsOne: true } || b is LlvmConstantInt { IsOne: true }) ||
                    compare.Predicate == LlvmCmpPredicate.ICmpNe && compare.Right is LlvmConstantInt { IsMinusOne: true } &&
                        (a is LlvmConstantInt { IsMinusOne: true } || b is LlvmConstantInt { IsMinusOne: true })) sum = lcall;
            }
            else if (compare.Right is LlvmExtractValueInstruction { Indices: [0] } right && CallsIntrinsic(right.AggregateOperand,
                LlvmIntrinsicKind.UAddWithOverflow, out var rcall) && compare.Predicate == LlvmCmpPredicate.ICmpUgt &&
                (compare.Left == rcall.GetArgument(0) || compare.Left == rcall.GetArgument(1))) sum = rcall;
            if (sum is null) return null;
            Count("icmp.intrinsic.uadd_overflow_extract");
            return new LlvmExtractValueInstruction(sum, [1]);
        }

        private LlvmInstruction? FoldICmpInvariantGroup(LlvmICmpInstruction compare)
        {
            if (compare.Left.Type is not LlvmPointerType pointer || LlvmFunction.NullPointerIsDefined(compare.Parent!.Parent, pointer.AddressSpace)) return null;
            if (!CallsIntrinsic(compare.Left, LlvmIntrinsicKind.LaunderInvariantGroup, out var launder) || compare.Right is not LlvmConstantPointerNull) return null;
            return new LlvmICmpInstruction(compare.Predicate, launder.GetArgument(0), compare.Right);
        }
    }
}
