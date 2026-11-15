// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// SimplifyAssociativeOrCommutative, preserving source rewrite order and flags.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? AlgebraSimplifyBin(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            LlvmInstructionSimplify.SimplifyBinOp(opcode, left, right, Query(context));
        private bool SimplifyAssociativeOrCommutative(LlvmBinaryOperator instruction)
        {
            Count("algebra.assoc_comm.queries");
            var opcode = instruction.Opcode;
            bool changed = false;
            while (true)
            {
                if (instruction.IsCommutative && GetComplexity(instruction.Left) < GetComplexity(instruction.Right))
                { instruction.SwapOperands(); changed = true; Count("algebra.commuted_complexity"); }
                if (instruction.IsCommutative && MatchSymmetricPair(instruction.Left, instruction.Right) is { } pair)
                {
                    ReplaceOperand(instruction, 0, pair.First); ReplaceOperand(instruction, 1, pair.Second);
                    changed = true; Count("algebra.symmetric_pair");
                }
                var left = instruction.Left as LlvmBinaryOperator;
                var right = instruction.Right as LlvmBinaryOperator;
                if (instruction.IsAssociative)
                {
                    if (left is not null && left.Opcode == opcode)
                    {
                        var a = left.Left; var b = left.Right; var c = instruction.Right;
                        if (TryReassociateAndFoldSymmetricPair(instruction, left, a, b, c))
                        { instruction.SwapOperands(); changed = true; Count("algebra.reassociated"); continue; }
                        if (AlgebraSimplifyBin(opcode, b, c, instruction) is { } value)
                        {
                            ReplaceOperand(instruction, 0, a); ReplaceOperand(instruction, 1, value);
                            bool nuw = AlgebraHasNUW(instruction) && AlgebraHasNUW(left);
                            bool nsw = MaintainNoSignedWrap(instruction, b, c) && AlgebraHasNSW(left);
                            if (opcode == LlvmOpcode.Or) instruction.IsDisjoint = false;
                            if (AlgebraOverflowing(opcode)) { instruction.HasNoUnsignedWrap = nuw; instruction.HasNoSignedWrap = nsw; }
                            changed = true; Count("algebra.reassociated"); continue;
                        }
                    }
                    if (right is not null && right.Opcode == opcode)
                    {
                        var a = instruction.Left; var b = right.Left; var c = right.Right;
                        if (TryReassociateAndFoldSymmetricPair(instruction, right, c, b, a))
                        { instruction.SwapOperands(); changed = true; Count("algebra.reassociated"); continue; }
                        if (AlgebraSimplifyBin(opcode, a, b, instruction) is { } value)
                        {
                            ReplaceOperand(instruction, 0, value); ReplaceOperand(instruction, 1, c);
                            if (!instruction.IsFloatingMathOperator) instruction.DropPoisonGeneratingFlags();
                            changed = true; Count("algebra.reassociated"); continue;
                        }
                    }
                }
                if (!instruction.IsAssociative || !instruction.IsCommutative) return changed;
                if (SimplifyAssocCastAssoc(instruction)) { changed = true; Count("algebra.reassociated"); continue; }
                if (left is not null && left.Opcode == opcode)
                {
                    var a = left.Left; var b = left.Right; var c = instruction.Right;
                    if (TryReassociateAndFoldSymmetricPair(instruction, left, b, a, c))
                    { changed = true; Count("algebra.reassociated"); continue; }
                    if (AlgebraSimplifyBin(opcode, c, a, instruction) is { } value)
                    {
                        ReplaceOperand(instruction, 0, value); ReplaceOperand(instruction, 1, b);
                        if (!instruction.IsFloatingMathOperator) instruction.DropPoisonGeneratingFlags();
                        changed = true; Count("algebra.reassociated"); continue;
                    }
                }
                if (right is not null && right.Opcode == opcode)
                {
                    var a = instruction.Left; var b = right.Left; var c = right.Right;
                    if (TryReassociateAndFoldSymmetricPair(instruction, right, b, c, a))
                    { changed = true; Count("algebra.reassociated"); continue; }
                    if (AlgebraSimplifyBin(opcode, c, a, instruction) is { } value)
                    {
                        ReplaceOperand(instruction, 0, b); ReplaceOperand(instruction, 1, value);
                        if (!instruction.IsFloatingMathOperator) instruction.DropPoisonGeneratingFlags();
                        changed = true; Count("algebra.reassociated"); continue;
                    }
                }
                if (left is not null && right is not null && left.Opcode == opcode && right.Opcode == opcode &&
                    left.HasOneUse && right.HasOneUse && left.Right is LlvmConstant c1 && right.Right is LlvmConstant c2 &&
                    LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, c1, c2, DL, Metrics) is { } combined)
                {
                    bool nuw = AlgebraHasNUW(instruction) && AlgebraHasNUW(left) && AlgebraHasNUW(right);
                    var created = new LlvmBinaryOperator(opcode, left.Left, right.Left)
                    { HasNoUnsignedWrap = nuw && opcode == LlvmOpcode.Add };
                    if (created.IsFloatingMathOperator)
                        created.FastMathFlags = instruction.FastMathFlags & left.FastMathFlags & right.FastMathFlags;
                    InsertNewInstWith(created, instruction); created.TakeName(right);
                    ReplaceOperand(instruction, 0, created); ReplaceOperand(instruction, 1, combined);
                    if (!instruction.IsFloatingMathOperator) instruction.DropPoisonGeneratingFlags();
                    if (nuw) instruction.HasNoUnsignedWrap = true;
                    changed = true; Count("algebra.constant_pairs_combined"); continue;
                }
                return changed;
            }
        }
    }
}
