// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: visitXor scalar identities.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldXorAndOrIdentities(LlvmBinaryOperator i)
        {
            if (i.Right.HasOneUse && LogicOther(i.Right, LlvmOpcode.Or, i.Left, out var orX1))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, orX1, Builder.CreateNot(i.Left)), "xor.or_absorption_rhs");
            if (i.Left.HasOneUse && LogicOther(i.Left, LlvmOpcode.Or, i.Right, out var orX0))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, orX0, Builder.CreateNot(i.Right)), "xor.or_absorption_lhs");
            if (i.Right.HasOneUse && LogicOther(i.Right, LlvmOpcode.And, i.Left, out var andX1))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, i.Left, Builder.CreateNot(andX1)), "xor.and_absorption_rhs");
            if (i.Right is not LlvmConstant && i.Left.HasOneUse && LogicOther(i.Left, LlvmOpcode.And, i.Right, out var andX0))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, i.Right, Builder.CreateNot(andX0)), "xor.and_absorption_lhs");
            for (int binding = 0; binding < 2; ++binding)
                for (int orientation = 0; orientation < 2; ++orientation)
                {
                    var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                    if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true } xor || !rhs.HasOneUse) continue;
                    var common = binding == 0 ? xor.Left : xor.Right; var other = binding == 0 ? xor.Right : xor.Left;
                    if (!LogicOther(rhs, LlvmOpcode.Or, common, out var c)) continue;
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateAnd(Builder.CreateNot(common), c), other), "xor.xor_or_common");
                }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && LogicPair(rhs, LlvmOpcode.Xor, and.Left, and.Right))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, and.Left, and.Right), "xor.and_xor_to_or");
            }
            if (LogicNot(i.Right, out var rightNot))
                for (int order = 0; order < 2; ++order)
                    if (LogicNotPairOperands(i.Left, LlvmOpcode.And, order != 0, out var a, out var b) && ReferenceEquals(a, rightNot))
                        return LogicResult(LogicNotInstruction(Builder.CreateAnd(a, b)), "xor.and_not_to_nand");
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                for (int order = 0; order < 2; ++order)
                    if (LogicNotPairOperands(lhs, LlvmOpcode.And, order != 0, out var b, out var a) && ReferenceEquals(rhs, a))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, a, b), "xor.not_and_self_to_or");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (!lhs.HasOneUse) continue;
                for (int order = 0; order < 2; ++order)
                    if (LogicNotPairOperands(lhs, LlvmOpcode.Or, order != 0, out var b, out var a) && ReferenceEquals(a, rhs))
                        return LogicResult(LogicNotInstruction(Builder.CreateAnd(a, b)), "xor.not_or_self_to_nand");
            }
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } left
                && i.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } right)
            {
                LlvmValue a = left.Left, b = left.Right, c = right.Left, d = right.Right;
                if (ReferenceEquals(b, c) || ReferenceEquals(b, d)) (a, b) = (b, a);
                if (ReferenceEquals(a, c)) (c, d) = (d, c);
                if (ReferenceEquals(a, d)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateXor(b, c), Builder.CreateNot(a)), "xor.or_common_to_and");
            }
            return null;
        }
        private LlvmInstruction? FoldXorLogicalSelect(LlvmBinaryOperator i)
        {
            if (!i.Type.IsIntegerTy(1) || !i.Left.HasOneUse || !i.Right.HasOneUse) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (!LogicLogical(lhs, true, out var a, out var b) || !LogicLogical(rhs, false, out var c, out var d)) continue;
                bool freeze = i.Left is LlvmSelectInstruction && i.Right is LlvmSelectInstruction && ReferenceEquals(b, d);
                if (ReferenceEquals(b, c) || ReferenceEquals(b, d)) (a, b) = (b, a);
                if (ReferenceEquals(a, c)) (c, d) = (d, c);
                if (!ReferenceEquals(a, d)) continue;
                if (freeze) a = Builder.CreateFreeze(a);
                return LogicSelect(a, Builder.CreateNot(b), c, "xor.logical_and_or_to_select");
            }
            return null;
        }
    }
}
