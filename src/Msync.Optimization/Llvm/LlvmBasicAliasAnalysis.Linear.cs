// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp
// GetLinearExpression. This is the source decomposition, not a new algebra solver.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    internal LinearExpression GetLinearExpression(CastedValue value, uint depth, LlvmDominatorTree? tree)
    {
        Count("aa.linear.visits");
        if (depth == 6) { Count("aa.linear.depth_limit"); return new(value); }
        if (value.V is LlvmConstantInt constant)
        {
            Count("aa.linear.constant");
            return new(value, LlvmAPInt.GetZero(value.BitWidth), value.EvaluateWith(constant.Value), true, true);
        }
        if (value.V is LlvmBinaryOperator binary && binary.Right is LlvmConstantInt rhsConstant)
        {
            var rhs = value.EvaluateWith(rhsConstant.Value);
            bool nuw = true, nsw = true;
            if (binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl)
            { nuw = binary.HasNoUnsignedWrap; nsw = binary.HasNoSignedWrap; }
            if (!value.CanDistributeOver(nuw, nsw)) { Count("aa.linear.extension_blocks_distribution"); return new(value); }
            if (value.TruncBits != 0) { nuw = false; nsw = false; }
            LinearExpression expression;
            switch (binary.Opcode)
            {
                case LlvmOpcode.Or when !binary.IsDisjoint:
                    Count("aa.linear.nondisjoint_or"); return new(value);
                case LlvmOpcode.Or:
                case LlvmOpcode.Add:
                    expression = GetLinearExpression(value.WithValue(binary.Left, false), depth + 1, tree);
                    expression.Offset = expression.Offset.SAddOverflow(rhs, out bool addOverflow);
                    expression.IsNUW &= nuw;
                    expression.IsNSW &= nsw && !addOverflow;
                    Count("aa.linear.add");
                    if (addOverflow) Count("aa.linear.offset_signed_overflow");
                    return expression;
                case LlvmOpcode.Sub:
                    expression = GetLinearExpression(value.WithValue(binary.Left, false), depth + 1, tree);
                    expression.Offset = expression.Offset.SSubOverflow(rhs, out bool subOverflow);
                    expression.IsNUW = false;
                    expression.IsNSW &= nsw && !subOverflow;
                    Count("aa.linear.sub");
                    if (subOverflow) Count("aa.linear.offset_signed_overflow");
                    return expression;
                case LlvmOpcode.Mul:
                    Count("aa.linear.mul");
                    return GetLinearExpression(value.WithValue(binary.Left, false), depth + 1, tree).Mul(rhs, nuw, nsw);
                case LlvmOpcode.Shl:
                    if (rhs.GetLimitedValue() > value.BitWidth) { Count("aa.linear.poison_shift"); return new(value); }
                    uint shiftAmount = (uint)rhs.GetLimitedValue();
                    Count("aa.linear.shl");
                    return GetLinearExpression(value.WithValue(binary.Left, nsw), depth + 1, tree)
                        .Mul(new LlvmAPInt(value.BitWidth, 1).Shl(shiftAmount), nuw, nsw && shiftAmount < value.BitWidth - 1);
                default:
                    Count("aa.linear.other_operator"); return new(value);
            }
        }
        if (value.V is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } zext)
        {
            Count("aa.linear.zext");
            return GetLinearExpression(value.WithZExtOfValue(zext.Operand, zext.IsNonNegative), depth + 1, tree);
        }
        if (value.V is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext)
        {
            Count("aa.linear.sext");
            return GetLinearExpression(value.WithSExtOfValue(sext.Operand), depth + 1, tree);
        }
        Count("aa.linear.leaf");
        return new(value);
    }
}
