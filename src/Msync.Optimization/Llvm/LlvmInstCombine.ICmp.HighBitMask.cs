// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp foldICmpWithHighBitMask.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpWithHighBitMask(LlvmICmpInstruction compare)
        {
            for (int side = 0; side != 2; ++side)
            {
                var mask = side == 0 ? compare.Left : compare.Right;
                var value = side == 0 ? compare.Right : compare.Left;
                var predicate = side == 0 ? compare.Predicate : LlvmCmpInstruction.GetSwappedPredicate(compare.Predicate);
                if (!mask.HasOneUse) continue;
                LlvmValue? shift = null;
                bool strict;
                if (mask is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } power)
                { shift = power.Right; strict = false; }
                else if (ICmpMatchNot(mask) is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsMinusOne: true } } inverse)
                { shift = inverse.Right; strict = true; }
                else if (mask is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmBinaryOperator
                    { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } high, Right: LlvmConstantInt { IsMinusOne: true } })
                { shift = high.Right; strict = true; }
                else continue;
                LlvmCmpPredicate result;
                if (predicate == (strict ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUle)) result = LlvmCmpPredicate.ICmpNe;
                else if (predicate == (strict ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUgt)) result = LlvmCmpPredicate.ICmpEq;
                else continue;
                var highBits = Builder.CreateBinary(LlvmOpcode.LShr, value, shift, value.Name + ".highbits");
                Count("icmp.high_bit_mask");
                return new LlvmICmpInstruction(result, highBits, ICmpZero(highBits));
            }
            return null;
        }
    }
}
