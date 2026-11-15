// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp 2189–2277.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldBinopWithRecurrence(LlvmBinaryOperator binary)
        {
            if (!binary.IsAssociative || binary.Left is not LlvmBinaryOperator left || binary.Right is not LlvmBinaryOperator right ||
                !left.HasNUses(2) || !right.HasNUses(2) || left.Opcode != binary.Opcode || right.Opcode != binary.Opcode ||
                !left.IsAssociative || !right.IsAssociative || left.Parent != right.Parent) return null;
            if (!LlvmValueTracking.MatchSimpleRecurrence(left, out var leftPhi, out var startLeft, out var stepLeft) || !leftPhi.HasOneUse ||
                !LlvmValueTracking.MatchSimpleRecurrence(right, out var rightPhi, out var startRight, out var stepRight) || !rightPhi.HasOneUse || leftPhi.Parent != rightPhi.Parent) return null;
            if (startLeft is not LlvmConstant initLeft || startRight is not LlvmConstant initRight || stepLeft is not LlvmConstant cLeft || stepRight is not LlvmConstant cRight) return null;
            var init = LlvmConstantFolding.ConstantFoldBinaryOpOperands(binary.Opcode, initLeft, initRight, DL, Metrics);
            var step = LlvmConstantFolding.ConstantFoldBinaryOpOperands(binary.Opcode, cLeft, cRight, DL, Metrics);
            if (init is null || step is null) return null;
            var phi = new LlvmPhiInstruction(leftPhi.Type, leftPhi.IncomingCount) { LogicalType = binary.LogicalType };
            phi.SetName("reduced.phi"); var result = new LlvmBinaryOperator(binary.Opcode, phi, step);
            if (binary.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FMul)
                result.FastMathFlags = left.FastMathFlags & right.FastMathFlags & binary.FastMathFlags;
            else
            {
                var flags = new LlvmOverflowTracking { AllKnownNonNegative = false, AllKnownNonZero = false };
                flags.MergeFlags(left); flags.MergeFlags(right); flags.MergeFlags(binary); flags.ApplyFlags(result);
            }
            result.TakeName(binary);
            for (int i = 0; i < leftPhi.IncomingCount; ++i)
            {
                var value = leftPhi.GetIncomingValue(i); var block = leftPhi.GetIncomingBlock(i);
                if (value == initLeft) phi.AddIncoming(init, block);
                else
                {
                    System.Diagnostics.Debug.Assert(value == left);
                    phi.AddIncoming(result, block);
                }
            }
            InsertNewInstWith(phi, leftPhi); InsertNewInstWith(result, left);
            EraseInstFromFunction(ReplaceInstUsesWith(left, LlvmPoisonValue.Get(left.Type))!);
            EraseInstFromFunction(ReplaceInstUsesWith(right, LlvmPoisonValue.Get(right.Type))!);
            EraseInstFromFunction(leftPhi); EraseInstFromFunction(rightPhi);
            Count("phi.binop.recurrence_reduced"); return ReplaceInstUsesWith(binary, result);
        }
    }
}
