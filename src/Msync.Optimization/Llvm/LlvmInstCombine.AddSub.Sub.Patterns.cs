// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: PatternMatch.h patterns used by visitSub.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SubBinary(LlvmValue value, LlvmOpcode opcode, out LlvmValue left, out LlvmValue right)
        {
            if (value is LlvmBinaryOperator binary && binary.Opcode == opcode)
            { left = binary.Left; right = binary.Right; return true; }
            if (value is LlvmConstantExpr expression && expression.Opcode == opcode && expression.OperandCount == 2)
            { left = expression.GetOperand(0)!; right = expression.GetOperand(1)!; return true; }
            left = right = null!; return false;
        }
        private static bool SubCommuted(LlvmValue value, LlvmOpcode opcode, LlvmValue specific, out LlvmValue other)
        {
            if (SubBinary(value, opcode, out var left, out var right))
            {
                if (left == specific) { other = right; return true; }
                if (right == specific) { other = left; return true; }
            }
            other = null!; return false;
        }
        private static bool SubPair(LlvmValue value, LlvmOpcode opcode, LlvmValue first, LlvmValue second) =>
            SubCommuted(value, opcode, first, out var other) && other == second;
        private static bool SubCast(LlvmValue value, LlvmOpcode opcode, out LlvmValue operand)
        {
            if (value is LlvmCastInstruction cast && cast.Opcode == opcode) { operand = cast.Operand; return true; }
            if (value is LlvmConstantExpr expression && expression.IsCast && expression.Opcode == opcode)
            { operand = expression.GetOperand(0)!; return true; }
            operand = null!; return false;
        }
        private static bool SubSExtLike(LlvmValue value, out LlvmValue operand) =>
            SubCast(value, LlvmOpcode.SExt, out operand) || value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, IsNonNegative: true } cast && (operand = cast.Operand) is not null;
        private static bool SubAddLike(LlvmValue value, out LlvmValue left, out LlvmValue right) =>
            SubBinary(value, LlvmOpcode.Add, out left, out right) || value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true } binary &&
            (left = binary.Left) is not null && (right = binary.Right) is not null;
        private static bool SubNSW(LlvmValue value) => value is LlvmBinaryOperator { HasNoSignedWrap: true } or LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true } || value is LlvmConstantExpr { HasNoSignedWrap: true };
        private static bool SubNUW(LlvmValue value) => value is LlvmBinaryOperator { HasNoUnsignedWrap: true } or LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true } || value is LlvmConstantExpr { HasNoUnsignedWrap: true };
        private static LlvmConstantInt? SubAPInt(LlvmValue value, bool allowPoison = false) =>
            value as LlvmConstantInt ?? (value.Type.IsVectorTy() ? (value as LlvmConstant)?.GetSplatValue(allowPoison) as LlvmConstantInt : null);
        private static bool SubZero(LlvmValue value) => SubAPInt(value, true)?.Value.IsZero == true;
        private static bool SubOnes(LlvmValue value) => SubAPInt(value, true)?.Value.IsAllOnes == true;
        private static bool SubOne(LlvmValue value) => SubAPInt(value, true)?.Value.IsOne == true;
        private static LlvmConstant SubConstant(LlvmType type, LlvmAPInt value) => type is LlvmVectorType vector
            ? LlvmConstantVector.GetSplat(vector.ElementCount, LlvmConstantInt.Get(type.Context, value)) : LlvmConstantInt.Get(type.Context, value);
        private static LlvmConstant SubUnit(LlvmType type) => SubConstant(type, new(type.GetScalarSizeInBits(), 1));
        private static bool SubNot(LlvmValue value, out LlvmValue operand)
        {
            if (SubBinary(value, LlvmOpcode.Xor, out var left, out var right))
            { if (SubOnes(right)) { operand = left; return true; } if (SubOnes(left)) { operand = right; return true; } }
            operand = null!; return false;
        }
        private static bool SubNeg(LlvmValue value, out LlvmValue operand) =>
            SubBinary(value, LlvmOpcode.Sub, out var left, out operand) && SubZero(left);
        private static bool SubIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, out LlvmCallInstruction call)
        {
            if (value is LlvmCallInstruction instruction && instruction.CalledFunction?.IntrinsicDescriptor?.Kind == kind) { call = instruction; return true; }
            call = null!; return false;
        }
        private static bool SubMinMaxWith(LlvmValue value, LlvmIntrinsicKind kind, LlvmValue specific, out LlvmValue other)
        {
            if (SubIntrinsic(value, kind, out var call))
            {
                if (call.GetArgument(0) == specific) { other = call.GetArgument(1); return true; }
                if (call.GetArgument(1) == specific) { other = call.GetArgument(0); return true; }
            }
            other = null!; return false;
        }
        private static bool SubNotMinSignedValue(LlvmConstant value)
        {
            if (value is LlvmConstantInt integer) return !integer.Value.IsMinSignedValue;
            if (value is LlvmConstantFP floating) return !floating.Value.BitcastToAPInt().IsMinSignedValue;
            if (value.Type is LlvmFixedVectorType vector)
            {
                for (uint index = 0; index < vector.NumElements; index++)
                    if (value.GetAggregateElement(index) is not { } element || !SubNotMinSignedValue(element)) return false;
                return true;
            }
            return value.Type.IsVectorTy() && value.GetSplatValue() is { } splat && SubNotMinSignedValue(splat);
        }
        private LlvmInstruction SubRewrite(LlvmInstruction result, string rule)
        { if (Metrics is not null) Count("addsub.sub.rewrite." + rule); return result; }
        private LlvmInstruction? SubReplace(LlvmBinaryOperator instruction, LlvmValue result, string rule)
        { if (Metrics is not null) Count("addsub.sub.rewrite." + rule); return ReplaceInstUsesWith(instruction, result); }
    }
}
