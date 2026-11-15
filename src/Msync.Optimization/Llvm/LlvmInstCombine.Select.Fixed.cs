// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectZeroOrFixedOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectZeroOrFixedOp(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmICmpInstruction compare || compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) ||
                !SelectIsZero(compare.Right)) return null;
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue, value = compare.Left;
            if (compare.Predicate == LlvmCmpPredicate.ICmpNe) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            if (whenTrue is not LlvmConstant || whenFalse is not LlvmInstruction instruction) return null;
            LlvmValue other; bool freeze;
            if (whenFalse is LlvmBinaryOperator binary && binary.Opcode is LlvmOpcode.Mul or LlvmOpcode.And)
            {
                if (ReferenceEquals(binary.Left, value)) other = binary.Right;
                else if (ReferenceEquals(binary.Right, value)) other = binary.Left;
                else return null;
                freeze = true;
            }
            else if (whenFalse is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor &&
                (descriptor.Kind == LlvmIntrinsicKind.FShl || descriptor.Kind == LlvmIntrinsicKind.FShr) &&
                ReferenceEquals(call.GetArgument(0), value) && ReferenceEquals(call.GetArgument(1), value))
            { other = call.GetArgument(2); freeze = true; }
            else if (whenFalse is LlvmCallInstruction minimum && minimum.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.UMin)
            {
                if (ReferenceEquals(minimum.GetArgument(0), value)) other = minimum.GetArgument(1);
                else if (ReferenceEquals(minimum.GetArgument(1), value)) other = minimum.GetArgument(0);
                else return null;
                freeze = true;
            }
            else if (whenFalse is LlvmBinaryOperator div && div.Opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem && ReferenceEquals(div.Left, value))
            { other = div.Right; freeze = false; }
            else return null;
            // In the scalar source slice mergeUndefsWith(C, zero) preserves C;
            // only zero or UndefValue (including poison) matches afterward.
            if (!SelectIsZero(whenTrue) && whenTrue is not LlvmUndefValue) return null;
            if (freeze)
            {
                LlvmFreezeInstruction frozen = new(other); frozen.SetName(other.Name + ".fr");
                InsertNewInstBefore(frozen, instruction);
                int operand = ReferenceEquals(instruction.GetOperand(0), other) ? 0 : ReferenceEquals(instruction.GetOperand(1), other) ? 1 : 2;
                ReplaceOperand(instruction, operand, frozen);
            }
            return ReplaceInstUsesWith(select, instruction);
        }
    }
}
