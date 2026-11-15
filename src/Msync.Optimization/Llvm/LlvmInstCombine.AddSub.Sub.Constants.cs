// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2664–2745.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SubConstants(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (left is LlvmConstant constant)
            {
                if (SubCast(right, LlvmOpcode.ZExt, out var boolean) && boolean.Type.GetScalarType().IsIntegerTy(1))
                    return SubRewrite(CreateSelectInstWithUnknownProfile(boolean, LlvmConstantExpr.GetSub(constant, SubUnit(instruction.Type)), constant), "constant_minus_zext_bool");
                if (SubCast(right, LlvmOpcode.SExt, out boolean) && boolean.Type.GetScalarType().IsIntegerTy(1))
                    return SubRewrite(CreateSelectInstWithUnknownProfile(boolean, LlvmConstantExpr.GetAdd(constant, SubUnit(instruction.Type)), constant), "constant_minus_sext_bool");
                if (SubNot(right, out var inverse))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, inverse, LlvmConstantExpr.GetAdd(constant, SubUnit(instruction.Type))), "constant_minus_not");
                if (right is LlvmSelectInstruction select && FoldOpIntoSelect(instruction, select) is { } threaded) return threaded;
                if (right is LlvmPhiInstruction phi && FoldOpIntoPhi(instruction, phi) is { } phis) return phis;
                if (SubBinary(right, LlvmOpcode.Sub, out var inner, out var x) && inner is LlvmConstant innerConstant && IsAddSubImmediateConstant(innerConstant))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, x, LlvmConstantExpr.GetSub(constant, innerConstant)), "constant_minus_constant_sub");
            }
            if (SubAPInt(left) is not { } lhs) return null;
            var c = lhs.Value;
            if (!c.IsZero && (c & (c + new LlvmAPInt(c.BitWidth, 1))).IsZero)
            {
                // Source explicitly excludes the dominating-condition cache for this reversible transform.
                var known = LlvmKnownBitsAnalysis.Compute(right, context: instruction, tree: DT, metrics: Metrics,
                    useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals);
                if ((c | known.Zero).IsAllOnes)
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Xor, right, left), "low_mask_minus_known_bits");
            }
            if (!right.HasOneUse || !SubBinary(right, LlvmOpcode.And, out var innerSub, out var mask) || SubAPInt(mask) is not { } c2 ||
                !SubBinary(innerSub, LlvmOpcode.Sub, out var innerConstantValue, out var value) || SubAPInt(innerConstantValue) is not { } c3 ||
                !(SubNUW(innerSub) || c2.Value.IsNegative && (-c2.Value).IsPowerOf2)) return null;
            var intersection = c2.Value & c3.Value;
            var previous = intersection - new LlvmAPInt(c.BitWidth, 1);
            var sum = c2.Value + c3.Value;
            if (!(c3.Value - previous).IsPowerOf2 || !(previous & ~sum).IsZero) return null;
            return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateAnd(value, SubConstant(instruction.Type, c2.Value)),
                SubConstant(instruction.Type, c - intersection)), "constant_minus_masked_sub");
        }
    }
}
