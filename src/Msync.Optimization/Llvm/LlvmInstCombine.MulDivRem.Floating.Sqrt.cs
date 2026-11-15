// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// getFSqrtDivOptPattern, isFSqrtDivToFMulLegal, convertFSqrtDivIntoFMul.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFSqrtDivIntoFMul(LlvmBinaryOperator instruction)
        {
            if (!(MdrSpecificFP(instruction.Left, 1) || MdrSpecificFP(instruction.Left, -1)) ||
                instruction.Right is not LlvmCallInstruction root || MdrSqrt(root) is not { } argument) return null;
            var squares = new HashSet<LlvmBinaryOperator>(ReferenceEqualityComparer.Instance);
            var roots = new HashSet<LlvmBinaryOperator>(ReferenceEqualityComparer.Instance);
            foreach (var user in instruction.Users)
                if (user is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } square && ReferenceEquals(square.Left, instruction) && ReferenceEquals(square.Right, instruction)) squares.Add(square);
            foreach (var user in root.Users)
                if (user is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } quotient && ReferenceEquals(quotient.Left, argument) && MdrSqrt(quotient.Right) is { } a && ReferenceEquals(a, argument)) roots.Add(quotient);
            if (squares.Count == 0 || roots.Count == 0) return null;
            var squareBlock = squares.First().Parent; var rootBlock = roots.First().Parent;
            var rootFlags = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoInfs;
            if (!MdrFPHas(root, rootFlags) || !MdrHas(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal | LlvmFastMathFlags.NoInfs) ||
                (!ReferenceEquals(instruction.Parent, squareBlock) && !ReferenceEquals(instruction.Parent, rootBlock)) ||
                squares.Any(value => !ReferenceEquals(value.Parent, squareBlock) || !MdrHas(value, LlvmFastMathFlags.AllowReassoc)) ||
                roots.Any(value => !ReferenceEquals(value.Parent, rootBlock) || !MdrHas(value, LlvmFastMathFlags.AllowReassoc))) return null;
            Count("instcombine.fdiv.sqrt_group_transform");
            Builder.SetInsertPoint(instruction);
            var division = (LlvmBinaryOperator)MdrBuildFP(LlvmOpcode.FDiv, LlvmConstantFP.Get(instruction.Type, 1), argument, LlvmFastMathFlags.None);
            var squareFlags = squares.First().FastMathFlags;
            foreach (var square in squares)
            {
                squareFlags &= square.FastMathFlags;
                ReplaceInstUsesWith(square, division); EraseInstFromFunction(square);
                Count("instcombine.fdiv.sqrt_square_rewrites");
            }
            division.FastMathFlags = squareFlags;
            var clone = (LlvmCallInstruction)root.Clone();
            root.Parent!.InsertBefore(clone, root);
            var quotientFlags = roots.First().FastMathFlags;
            foreach (var quotient in roots)
            {
                quotientFlags &= quotient.FastMathFlags;
                ReplaceInstUsesWith(quotient, clone); EraseInstFromFunction(quotient);
                Count("instcombine.fdiv.sqrt_quotient_rewrites");
            }
            clone.FastMathFlags = quotientFlags;
            var multiply = MdrBuildFP(LlvmOpcode.FMul, division, clone, LlvmFastMathFlags.None);
            LlvmInstruction result = (LlvmInstruction)(MdrSpecificFP(instruction.Left, -1) ? Builder.CreateFNeg(multiply) : multiply);
            var rewriteMask = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal | LlvmFastMathFlags.AllowContract | LlvmFastMathFlags.ApproxFunc;
            var valueMask = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoSignedZeros;
            ((ILlvmFastMathInstruction)result).FastMathFlags = (rewriteMask & squareFlags & quotientFlags) | (valueMask & (squareFlags | quotientFlags));
            result.Source = instruction.Source;
            return ReplaceInstUsesWith(instruction, result);
        }
    }
}
