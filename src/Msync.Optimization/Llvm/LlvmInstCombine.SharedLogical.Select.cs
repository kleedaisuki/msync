// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// foldBinopOfSextBoolToSelect; InstCombineAndOrXor.cpp FoldOrOfLogicalAnds.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldBinopOfSextBoolToSelect(LlvmBinaryOperator operation)
        {
            if (operation.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt } extension ||
                !extension.Operand.Type.IsIntOrIntVectorTy(1) || operation.Right is not LlvmConstant constant ||
                !SharedLogicalImmediateConstant(constant)) return null;
            LlvmValue whenTrue = Builder.CreateBinOp(operation.Opcode, LlvmConstant.GetAllOnesValue(operation.Type), constant);
            LlvmValue whenFalse = Builder.CreateBinOp(operation.Opcode, LlvmConstant.GetNullValue(operation.Type), constant);
            Count("logical.sext_bool_constant_to_select");
            return CreateSelectInstWithUnknownProfile(extension.Operand, whenTrue, whenFalse);
        }
        private static bool SharedLogicalImmediateConstant(LlvmConstant constant)
        {
            static bool ContainsExpression(LlvmConstant value)
            {
                if (value is LlvmConstantExpr) return true;
                if (value.Type is not LlvmFixedVectorType vector) return false;
                for (uint i = 0; i < vector.NumElements; ++i)
                    if (value.GetAggregateElement(i) is LlvmConstantExpr) return true;
                return false;
            }
            if (!ContainsExpression(constant)) return true;
            return constant.Type.IsVectorTy() && constant.GetSplatValue(allowPoison: true) is { } splat && !ContainsExpression(splat);
        }
        private static bool SharedLogicalAndNot(LlvmValue value, out LlvmValue condition, out LlvmValue other)
        {
            condition = other = null!;
            if (!SharedLogicalMatch(value, true, out var left, out var right)) return false;
            if (SharedLogicalNot(left, out condition)) { other = right; return true; }
            if (SharedLogicalNot(right, out condition)) { other = left; return true; }
            return false;
        }
        private static bool SharedLogicalAndSpecific(LlvmValue value, LlvmValue condition, out LlvmValue other)
        {
            other = null!;
            if (!SharedLogicalMatch(value, true, out var left, out var right)) return false;
            if (left == condition) { other = right; return true; }
            if (right == condition) { other = left; return true; }
            return false;
        }
        private LlvmInstruction? FoldOrOfLogicalAnds(LlvmValue left, LlvmValue right)
        {
            if (SharedLogicalAndNot(right, out var condition, out var whenFalse) &&
                SharedLogicalAndSpecific(left, condition, out var whenTrue))
            {
                if (left is LlvmSelectInstruction first && right is LlvmSelectInstruction second &&
                    SharedLogicalNot(second.TrueValue, out var inverse) && inverse == first.TrueValue)
                    condition = Builder.CreateFreeze(condition);
                // This represented IR has no branch-profile metadata graph to
                // copy or swap. Source's profile branches have identical values.
                Count("logical.or_and_to_select");
                return CreateSelectInstWithUnknownProfile(condition, whenTrue, whenFalse);
            }
            if (SharedLogicalAndNot(left, out condition, out whenFalse) &&
                SharedLogicalAndSpecific(right, condition, out whenTrue))
            {
                if (left is LlvmSelectInstruction first && right is LlvmSelectInstruction second &&
                    SharedLogicalNot(first.TrueValue, out var inverse) && inverse == second.TrueValue)
                    condition = Builder.CreateFreeze(condition);
                Count("logical.or_and_to_select_commuted");
                return CreateSelectInstWithUnknownProfile(condition, whenTrue, whenFalse);
            }
            return null;
        }
    }
}
