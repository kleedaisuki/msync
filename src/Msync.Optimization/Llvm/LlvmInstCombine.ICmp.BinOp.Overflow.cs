// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 5125-5210.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldMultiplicationOverflowCheck(LlvmICmpInstruction compare)
        {
            Count("icmp.binop.mul-overflow.queries");
            for (int side = 0; side < 2; side++)
            {
                var value = side == 0 ? compare.Left : compare.Right; var y = side == 0 ? compare.Right : compare.Left;
                var pred = side == 0 ? compare.Predicate : LlvmCmpInstruction.GetSwappedPredicate(compare.Predicate);
                if (value is not LlvmBinaryOperator { HasOneUse: true } div) continue;
                LlvmBinaryOperator? mul = null; LlvmValue x; bool invert;
                if (!ICmpIsEquality(pred) && div.Opcode == LlvmOpcode.UDiv && ICmpConstantMatches(div.Left, bits => bits.IsAllOnes))
                {
                    if (pred is not (LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge)) return null;
                    x = div.Right; invert = pred == LlvmCmpPredicate.ICmpUge;
                }
                else if (ICmpIsEquality(pred) && div.Opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv &&
                    div.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } product && BinMatchCommon(product, LlvmOpcode.Mul, y, out x) && x == div.Right)
                { mul = product; invert = pred == LlvmCmpPredicate.ICmpEq; }
                else continue;
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                bool replace = mul is not null && !mul.HasOneUse;
                if (replace) Builder.SetInsertPoint(mul!);
                var call = Builder.CreateIntrinsic(div.Opcode == LlvmOpcode.UDiv ? LlvmIntrinsicKind.UMulWithOverflow : LlvmIntrinsicKind.SMulWithOverflow, [x, y], "mul");
                if (replace) ReplaceInstUsesWith(mul!, Builder.CreateExtractValue(call, 0, "mul.val"));
                LlvmValue result = Builder.CreateExtractValue(call, 1, "mul.ov");
                if (invert) result = Builder.CreateNot(result, "mul.not.ov");
                if (replace) EraseInstFromFunction(mul!);
                Count(invert ? "icmp.binop.mul-overflow.inverted" : "icmp.binop.mul-overflow.direct");
                return result;
            }
            return null;
        }
    }
}
