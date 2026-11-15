// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp,
// maintainNoSignedWrap, simplifyAssocCastAssoc and symmetric-pair helpers.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool AlgebraOverflowing(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl;
        private static bool AlgebraHasNUW(LlvmBinaryOperator instruction) => AlgebraOverflowing(instruction.Opcode) && instruction.HasNoUnsignedWrap;
        private static bool AlgebraHasNSW(LlvmBinaryOperator instruction) => AlgebraOverflowing(instruction.Opcode) && instruction.HasNoSignedWrap;
        private static bool MaintainNoSignedWrap(LlvmBinaryOperator instruction, LlvmValue left, LlvmValue right)
        {
            if (!AlgebraHasNSW(instruction) || left is not LlvmConstantInt a || right is not LlvmConstantInt b) return false;
            bool overflow;
            switch (instruction.Opcode)
            {
                case LlvmOpcode.Add: _ = a.Value.SAddOverflow(b.Value, out overflow); break;
                case LlvmOpcode.Sub: _ = a.Value.SSubOverflow(b.Value, out overflow); break;
                case LlvmOpcode.Mul: _ = a.Value.SMulOverflow(b.Value, out overflow); break;
                default: return false;
            }
            return !overflow;
        }
        private bool SimplifyAssocCastAssoc(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } cast ||
                instruction.Opcode is not (LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor) ||
                cast.Operand is not LlvmBinaryOperator inner || !inner.HasOneUse || inner.Opcode != instruction.Opcode ||
                instruction.Right is not LlvmConstant a || inner.Right is not LlvmConstant b) return false;
            var castConstant = LlvmConstantFolding.ConstantFoldCastOperand(cast.Opcode, b, a.Type, DL, Metrics);
            if (castConstant is null) return false;
            var folded = LlvmConstantFolding.ConstantFoldBinaryOpOperands(instruction.Opcode, a, castConstant, DL, Metrics);
            if (folded is null) return false;
            ReplaceOperand(cast, 0, inner.Left); ReplaceOperand(instruction, 1, folded);
            instruction.DropPoisonGeneratingFlags(); cast.DropPoisonGeneratingFlags();
            Count("algebra.assoc_cast"); return true;
        }
        private static (LlvmValue First, LlvmValue Second)? MatchSymmetricPair(LlvmValue left, LlvmValue right)
        {
            if (left is not LlvmInstruction a || right is not LlvmInstruction b || a.Opcode != b.Opcode) return null;
            if (a is LlvmPhiInstruction pa && b is LlvmPhiInstruction pb)
            {
                if (pa.Parent != pb.Parent || pa.IncomingCount < 2 || pa.IncomingCount != pb.IncomingCount) return null;
                for (int i = 0; i < pa.IncomingCount; ++i) if (pa.GetIncomingBlock(i) != pb.GetIncomingBlock(i)) return null;
                var first = pa.GetIncomingValue(0); var second = pb.GetIncomingValue(0);
                for (int i = 1; i < pa.IncomingCount; ++i)
                {
                    var x = pa.GetIncomingValue(i); var y = pb.GetIncomingValue(i);
                    if (!(first == x && second == y || first == y && second == x)) return null;
                }
                return (first, second);
            }
            if (a is LlvmSelectInstruction sa && b is LlvmSelectInstruction sb)
                return sa.Condition == sb.Condition && sa.TrueValue == sb.FalseValue && sa.FalseValue == sb.TrueValue
                    ? (sa.TrueValue, sa.FalseValue) : null;
            if (a is not LlvmCallInstruction ca || b is not LlvmCallInstruction cb ||
                ca.CalledFunction?.IntrinsicDescriptor is not { } da || cb.CalledFunction?.IntrinsicDescriptor is not { } db ||
                !LlvmIntrinsic.IsMinMax(da.Kind) || !LlvmIntrinsic.IsMinMax(db.Kind) ||
                LlvmIntrinsic.GetMinMaxPredicate(da.Kind) != LlvmCmpInstruction.GetSwappedPredicate(LlvmIntrinsic.GetMinMaxPredicate(db.Kind))) return null;
            var lhs = ca.GetArgument(0); var rhs = ca.GetArgument(1);
            return lhs == cb.GetArgument(0) && rhs == cb.GetArgument(1) || lhs == cb.GetArgument(1) && rhs == cb.GetArgument(0)
                ? (lhs, rhs) : null;
        }
        private bool TryReassociateAndFoldSymmetricPair(LlvmBinaryOperator outer, LlvmBinaryOperator inner,
            LlvmValue inner0, LlvmValue inner1, LlvmValue outerValue)
        {
            Debug.Assert(outer.IsAssociative && outer.IsCommutative && outer.Opcode == inner.Opcode);
            if (!inner.IsAssociative || !inner.HasOneUse || MatchSymmetricPair(inner1, outerValue) is not { } pair) return false;
            int operand = outer.Left == outerValue ? 0 : 1;
            ReplaceOperand(inner, 0, pair.First); ReplaceOperand(inner, 1, pair.Second);
            inner.DropPoisonGeneratingAnnotations(); inner.DropUBImplyingAttrsAndMetadata();
            ReplaceOperand(outer, operand, inner0);
            if (!outer.IsFloatingMathOperator) outer.DropPoisonGeneratingFlags();
            outer.DropUBImplyingAttrsAndMetadata(); Count("algebra.reassoc_symmetric_pair"); return true;
        }
    }
}
