// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp foldVectorBinop/foldSpliceBinOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldVectorBinop(LlvmBinaryOperator instruction)
        {
            if (instruction.Type is not LlvmVectorType) return null;
            Count("vector_binop.queries");
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (FoldConstantsThroughSubvectorInsertSplat(instruction, left, right, false) is { } lhsFold) return lhsFold;
            if (FoldConstantsThroughSubvectorInsertSplat(instruction, right, left, true) is { } rhsFold) return rhsFold;
            if (MatchVectorReverse(left, out var first))
            {
                if (MatchVectorReverse(right, out var second) &&
                    (left.HasOneUse || right.HasOneUse || left == right && left.HasNUses(2)))
                    return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.VectorReverse, first!, second!);
                if (left.HasOneUse && LlvmValueTracking.IsSplatValue(right, metrics: Metrics))
                    return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.VectorReverse, first!, right);
            }
            else if (LlvmValueTracking.IsSplatValue(left, metrics: Metrics) && right.HasOneUse && MatchVectorReverse(right, out var reversed))
                return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.VectorReverse, left, reversed!);
            if (MatchVectorVpReverse(left, out var vpLeft, out var evl))
            {
                if (MatchVectorVpReverse(right, out var vpRight, out var otherEvl) && evl == otherEvl &&
                    (left.HasOneUse || right.HasOneUse || left == right && left.HasNUses(2)))
                    return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.ExperimentalVpReverse, vpLeft!, vpRight!, evl);
                if (left.HasOneUse && LlvmValueTracking.IsSplatValue(right, metrics: Metrics))
                    return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.ExperimentalVpReverse, vpLeft!, right, evl);
            }
            else if (LlvmValueTracking.IsSplatValue(left, metrics: Metrics) && MatchVectorVpReverse(right, out var vpReverse, out var reverseEvl))
                return CreateVectorBinopIntrinsic(instruction, LlvmIntrinsicKind.ExperimentalVpReverse, left, vpReverse!, reverseEvl);
            if (FoldSpliceBinop(instruction, LlvmIntrinsicKind.VectorSpliceLeft) is { } spliceLeft) return spliceLeft;
            if (FoldSpliceBinop(instruction, LlvmIntrinsicKind.VectorSpliceRight) is { } spliceRight) return spliceRight;
            if (!LlvmValueTracking.IsSafeToSpeculativelyExecuteWithVariableReplaced(instruction, metrics: Metrics)) return null;
            if (left is LlvmShuffleVectorInstruction { Right: LlvmPoisonValue } lhs && right is LlvmShuffleVectorInstruction { Right: LlvmPoisonValue } rhs &&
                lhs.ShuffleMask.SequenceEqual(rhs.ShuffleMask) && instruction.Type == lhs.Left.Type && lhs.Left.Type == rhs.Left.Type &&
                (lhs.HasOneUse || rhs.HasOneUse || lhs == rhs))
                return CreateVectorBinopShuffle(instruction, lhs.Left, rhs.Left, lhs.ShuffleMask);
            if (instruction.IsCommutative && left is LlvmShuffleVectorInstruction lselect && right is LlvmShuffleVectorInstruction rselect &&
                lselect.Left == rselect.Right && lselect.Right == rselect.Left && lselect.ShuffleMask.SequenceEqual(rselect.ShuffleMask) &&
                lselect.IsSelect && rselect.IsSelect && !lselect.ShuffleMask.Contains(LlvmShuffleVectorInstruction.PoisonMaskElem) &&
                !rselect.ShuffleMask.Contains(LlvmShuffleVectorInstruction.PoisonMaskElem))
            {
                var replacement = new LlvmBinaryOperator(instruction.Opcode, lselect.Left, lselect.Right);
                replacement.CopyIRFlags(instruction); Count("vector_binop.select_shuffles_removed"); return replacement;
            }
            LlvmShuffleVectorInstruction? shuffled = null;
            LlvmConstant? constant = null;
            if (left is LlvmShuffleVectorInstruction { Right: LlvmPoisonValue, HasOneUse: true } leftShuffle && IsVectorImmediateConstant(right))
            { shuffled = leftShuffle; constant = (LlvmConstant)right; }
            else if (right is LlvmShuffleVectorInstruction { Right: LlvmPoisonValue, HasOneUse: true } rightShuffle && IsVectorImmediateConstant(left))
            { shuffled = rightShuffle; constant = (LlvmConstant)left; }
            if (shuffled is not null && UnshuffleConstant(shuffled.ShuffleMask, constant!, (LlvmVectorType)shuffled.Left.Type) is { } unshuffled)
            {
                bool constantRight = right is LlvmConstant;
                if (shuffled.Left.Type is LlvmFixedVectorType && IsVectorIntegerDivRem(instruction.Opcode))
                    unshuffled = GetSafeVectorConstantForBinop(instruction.Opcode, unshuffled, constantRight);
                return CreateVectorBinopShuffle(instruction, constantRight ? shuffled.Left : unshuffled,
                    constantRight ? unshuffled : shuffled.Left, shuffled.ShuffleMask);
            }
            if (!instruction.IsAssociative || !instruction.IsCommutative) return null;
            if (right is LlvmShuffleVectorInstruction) (left, right) = (right, left);
            if (left is not LlvmShuffleVectorInstruction { Right: LlvmUndefValue, HasOneUse: true } splat || splat.Left.Type != instruction.Type ||
                !MatchVectorSplatOrPoisonMask(splat.ShuffleMask, out int splatIndex) ||
                right is not LlvmBinaryOperator { HasOneUse: true } nested || nested.Opcode != instruction.Opcode) return null;
            LlvmValue y = nested.Left, other = nested.Right;
            if (LlvmValueTracking.IsSplatValue(other, splatIndex, metrics: Metrics)) (y, other) = (other, y);
            else if (!LlvmValueTracking.IsSplatValue(y, splatIndex, metrics: Metrics)) return null;
            var newBinary = Builder.CreateBinOp(instruction.Opcode, splat.Left, y);
            var newSplat = Builder.CreateShuffleVector(newBinary, Enumerable.Repeat(splatIndex, splat.ShuffleMask.Count).ToArray());
            var result = new LlvmBinaryOperator(instruction.Opcode, newSplat, other);
            if (result.IsFloatingMathOperator) { result.FastMathFlags = instruction.FastMathFlags; result.AndIRFlags(right); }
            if (newBinary is LlvmBinaryOperator binary) binary.CopyIRFlags(result);
            Count("vector_binop.splat_reassociated");
            return result;
        }

        private LlvmInstruction? FoldConstantsThroughSubvectorInsertSplat(LlvmBinaryOperator instruction,
            LlvmValue maybeInsert, LlvmValue maybeSplat, bool splatLeft)
        {
            if (MatchVectorConstantSplat(maybeSplat) is not { } splat ||
                maybeInsert is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } insert || kind != LlvmIntrinsicKind.VectorInsert ||
                insert.GetArgument(0) is not LlvmConstant destination || insert.GetArgument(1) is not LlvmConstant subvector) return null;
            var subResult = ConstantFoldVectorBinopWithSplat(instruction.Opcode, subvector, splat, splatLeft);
            var destinationResult = ConstantFoldVectorBinopWithSplat(instruction.Opcode, destination, splat, splatLeft);
            if (subResult is null || destinationResult is null) return null;
            var result = Builder.CreateInsertVector(destinationResult.Type, destinationResult, subResult, insert.GetArgument(2));
            Count("vector_binop.subvector_constants_folded");
            return ReplaceInstUsesWith(instruction, result);
        }

        private LlvmInstruction CreateVectorBinopIntrinsic(LlvmBinaryOperator instruction, LlvmIntrinsicKind kind,
            LlvmValue left, LlvmValue right, LlvmValue? evl = null, LlvmValue? offset = null)
        {
            var value = Builder.CreateBinOp(instruction.Opcode, left, right, instruction.Name);
            if (value is LlvmBinaryOperator binary) binary.CopyIRFlags(instruction);
            LlvmValue[] arguments;
            if (kind == LlvmIntrinsicKind.ExperimentalVpReverse)
            {
                var count = ((LlvmVectorType)value.Type).ElementCount;
                var mask = Builder.CreateVectorSplat(count, LlvmConstantInt.GetTrue(value.Context));
                arguments = [value, mask, evl!];
            }
            else if (offset is not null) arguments = [value, LlvmPoisonValue.Get(value.Type), offset];
            else arguments = [value];
            Count("vector_binop.intrinsic_moved");
            return LlvmIntrinsic.CreateIntrinsic(kind, arguments, value.Type, F.Parent!, Metrics);
        }

        private LlvmInstruction CreateVectorBinopShuffle(LlvmBinaryOperator instruction, LlvmValue left, LlvmValue right,
            IReadOnlyList<int> mask)
        {
            var value = Builder.CreateBinOp(instruction.Opcode, left, right);
            if (value is LlvmBinaryOperator binary) binary.CopyIRFlags(instruction);
            Count("vector_binop.shuffle_moved");
            return new LlvmShuffleVectorInstruction(value, mask);
        }

        private LlvmInstruction? FoldSpliceBinop(LlvmBinaryOperator instruction, LlvmIntrinsicKind kind)
        {
            if (MatchVectorSplice(instruction.Left, kind, out var left, out var offset))
            {
                if (MatchVectorSplice(instruction.Right, kind, out var right, out var otherOffset) && offset == otherOffset &&
                    (instruction.Left.HasOneUse || instruction.Right.HasOneUse || instruction.Left == instruction.Right && instruction.Left.HasNUses(2)))
                    return CreateVectorBinopIntrinsic(instruction, kind, left!, right!, offset: offset);
                if (instruction.Left.HasOneUse && LlvmValueTracking.IsSplatValue(instruction.Right, metrics: Metrics))
                    return CreateVectorBinopIntrinsic(instruction, kind, left!, instruction.Right, offset: offset);
            }
            else if (LlvmValueTracking.IsSplatValue(instruction.Left, metrics: Metrics) && instruction.Right.HasOneUse &&
                MatchVectorSplice(instruction.Right, kind, out var right, out var rightOffset))
                return CreateVectorBinopIntrinsic(instruction, kind, instruction.Left, right!, offset: rightOffset);
            return null;
        }

        private static bool MatchVectorReverse(LlvmValue value, out LlvmValue? vector)
        {
            vector = value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } call && kind == LlvmIntrinsicKind.VectorReverse
                ? call.GetArgument(0) : null;
            return vector is not null;
        }

        private static bool MatchVectorVpReverse(LlvmValue value, out LlvmValue? vector, out LlvmValue? evl)
        {
            vector = evl = null;
            if (value is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } call || kind != LlvmIntrinsicKind.ExperimentalVpReverse ||
                call.GetArgument(1) is not LlvmConstant mask || mask.GetSplatValue(true) is not LlvmConstantInt { Value.IsAllOnes: true }) return false;
            vector = call.GetArgument(0); evl = call.GetArgument(2); return true;
        }

        private static bool MatchVectorSplice(LlvmValue value, LlvmIntrinsicKind kind, out LlvmValue? vector, out LlvmValue? offset)
        {
            vector = offset = null;
            if (value is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor?.Kind != kind || call.GetArgument(1) is not LlvmPoisonValue) return false;
            vector = call.GetArgument(0); offset = call.GetArgument(2); return true;
        }

        private static bool MatchVectorSplatOrPoisonMask(IReadOnlyList<int> mask, out int index)
        {
            index = -1;
            foreach (int lane in mask) if (lane != LlvmShuffleVectorInstruction.PoisonMaskElem) { index = lane; break; }
            if (index == -1) return false;
            foreach (int lane in mask) if (lane != LlvmShuffleVectorInstruction.PoisonMaskElem && lane != index) return false;
            return true;
        }

        private static LlvmConstant? MatchVectorConstantSplat(LlvmValue value)
        {
            if (value is LlvmConstant constant) return constant.GetSplatValue();
            return value is LlvmShuffleVectorInstruction { IsZeroEltSplat: true,
                Left: LlvmInsertElementInstruction { IndexOperand: LlvmConstantInt { IsZero: true }, InsertedElementOperand: LlvmConstant element } }
                ? element : null;
        }

        private static bool IsVectorImmediateConstant(LlvmValue value)
        {
            if (value is not LlvmConstant constant) return false;
            if (constant is not LlvmConstantExpr && !constant.ContainsConstantExpression()) return true;
            return constant.Type.IsVectorTy() && constant.GetSplatValue(true) is { } splat && splat is not LlvmConstantExpr && !splat.ContainsConstantExpression();
        }
        private static bool IsVectorIntegerDivRem(LlvmOpcode opcode) => opcode is LlvmOpcode.SDiv or LlvmOpcode.UDiv or LlvmOpcode.SRem or LlvmOpcode.URem;
    }
}
