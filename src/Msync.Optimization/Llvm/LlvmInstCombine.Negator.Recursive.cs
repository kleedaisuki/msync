// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineNegator.cpp
// visitImpl recursive represented scalar cases.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed partial class Negator
        {
            private LlvmValue? VisitRecursive(LlvmInstruction instruction, bool isNSW, uint depth)
            {
                string name = instruction.Name + ".neg";
                switch (instruction)
                {
                    case LlvmFreezeInstruction freeze:
                        return NegateValue(freeze.Operand, isNSW, depth + 1) is { } frozen
                            ? builder.CreateFreeze(frozen, name) : null;
                    case LlvmPhiInstruction phi:
                    {
                        LlvmValue[] incoming = new LlvmValue[phi.IncomingCount];
                        for (int index = 0; index < incoming.Length; ++index)
                        {
                            // DT.dominates(BB, incoming Use) checks the PHI edge,
                            // not the block defining the incoming value.
                            if (owner.DT.Dominates(phi.Parent, phi.GetIncomingBlock(index))) return null;
                            if (NegateValue(phi.GetIncomingValue(index), isNSW, depth + 1) is not { } negated) return null;
                            incoming[index] = negated;
                        }
                        var result = builder.CreatePhi(phi.Type, phi.IncomingCount, name);
                        for (int index = 0; index < incoming.Length; ++index) result.AddIncoming(incoming[index], phi.GetIncomingBlock(index));
                        return result;
                    }
                    case LlvmSelectInstruction select:
                    {
                        if (LlvmKnownBitsAnalysis.IsKnownNegation(select.TrueValue, select.FalseValue, needNSW: false, allowPoison: false))
                        {
                            var result = (LlvmSelectInstruction)select.Clone();
                            (result.TrueValue, result.FalseValue) = (result.FalseValue, result.TrueValue);
                            result.SetName(name);
                            if (MatchNegation(result.TrueValue, out LlvmValue source) && ReferenceEquals(source, result.FalseValue))
                                ((LlvmInstruction)result.TrueValue).DropPoisonGeneratingFlags();
                            else if (MatchNegation(result.FalseValue, out source) && ReferenceEquals(source, result.TrueValue))
                                ((LlvmInstruction)result.FalseValue).DropPoisonGeneratingFlags();
                            else
                            {
                                ((LlvmInstruction)result.TrueValue).DropPoisonGeneratingFlags();
                                ((LlvmInstruction)result.FalseValue).DropPoisonGeneratingFlags();
                            }
                            return builder.Insert(result);
                        }
                        if (NegateValue(select.TrueValue, isNSW, depth + 1) is not { } whenTrue) return null;
                        if (NegateValue(select.FalseValue, isNSW, depth + 1) is not { } whenFalse) return null;
                        return builder.CreateSelect(select.Condition, whenTrue, whenFalse, name);
                    }
                    case LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } cast:
                        return NegateValue(cast.Operand, false, depth + 1) is { } truncated
                            ? builder.CreateTrunc(truncated, cast.Type, name) : null;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift:
                    {
                        isNSW &= shift.HasNoSignedWrap;
                        if (NegateValue(shift.Left, isNSW, depth + 1) is { } negated)
                            return Shift(LlvmOpcode.Shl, negated, shift.Right, name, isNSW);
                        if (shift.Right is not LlvmConstant amount || !IsAddSubImmediateConstant(amount) || !trulyNegating) return null;
                        var allOnes = LlvmConstantInt.Get(amount.Context, LlvmAPInt.GetAllOnes(amount.Type.GetIntegerBitWidth()));
                        return builder.CreateMul(shift.Left, Shift(LlvmOpcode.Shl, allOnes, amount), name, nsw: isNSW);
                    }
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: false }:
                        return null;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Or } disjoint:
                        var sorted = SortedOperands(disjoint);
                        if (sorted.Right is LlvmConstantInt { IsOne: true }) return builder.CreateNot(sorted.Left, name);
                        return NegateAdd(disjoint, depth, name);
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add:
                        return NegateAdd(add, depth, name);
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor:
                    {
                        var operands = SortedOperands(xor);
                        if (!trulyNegating || operands.Right is not LlvmConstant constant) return null;
                        LlvmValue inverted = builder.CreateNot(constant);
                        LlvmValue complemented = builder.CreateXor(operands.Left, inverted);
                        return builder.CreateAdd(complemented, One(complemented.Type), name);
                    }
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } multiply:
                    {
                        var operands = SortedOperands(multiply);
                        LlvmValue negated, other;
                        if (NegateValue(operands.Right, false, depth + 1) is { } right) { negated = right; other = operands.Left; }
                        else if (NegateValue(operands.Left, false, depth + 1) is { } left) { negated = left; other = operands.Right; }
                        else return null;
                        return builder.CreateMul(negated, other, name, nsw: isNSW && multiply.HasNoSignedWrap);
                    }
                    default: return null;
                }
            }
            private LlvmValue? NegateAdd(LlvmBinaryOperator operation, uint depth, string name)
            {
                LlvmValue? left = NegateValue(operation.Left, false, depth + 1);
                if (left is null && !trulyNegating) return null;
                LlvmValue? right = NegateValue(operation.Right, false, depth + 1);
                if (right is null && !trulyNegating) return null;
                if (left is not null && right is not null) return builder.CreateAdd(left, right, name);
                if (left is null && right is null) return null;
                return builder.CreateSub(left ?? right!, left is null ? operation.Left : operation.Right, name);
            }
        }
    }
}
