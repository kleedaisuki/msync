// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp 1923–2187,2279–2390.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyInstructionWithPHI(LlvmInstruction instruction, LlvmPhiInstruction phi,
            LlvmValue input, LlvmBasicBlock incomingBlock)
        {
            var operands = instruction.Operands.Select(value => value == phi ? input : value!.DoPHITranslation(phi.Parent!, incomingBlock)).ToArray();
            var result = LlvmInstructionSimplify.SimplifyInstructionWithOperands(instruction, operands, Query(incomingBlock.GetTerminator()));
            if (result is not null && result != phi && result.SubclassId != (byte)LlvmValueKind.ConstantExprVal) return result;
            if (incomingBlock.GetTerminator() is LlvmCondBrInstruction branch && branch.GetSuccessor(0) != branch.GetSuccessor(1) && instruction is LlvmICmpInstruction compare)
            {
                bool truth = branch.GetSuccessor(0) == phi.Parent;
                if (LlvmInstructionSimplify.IsImpliedConditionForInstCombine(branch.Condition, compare.Predicate, operands[0], operands[1], new(DL, null, Metrics, null), truth) is { } implied)
                    return LlvmConstantInt.GetBool(instruction.Context, implied);
            }
            return null;
        }
        private LlvmInstruction? FoldOpIntoPhi(LlvmInstruction instruction, LlvmPhiInstruction phi, bool allowMultipleUses = false)
        {
            if (phi.IncomingCount == 0) return null;
            bool oneUse = phi.HasOneUse, identicalUsers = false;
            if (!allowMultipleUses && !oneUse)
            {
                foreach (var user in phi.Users.Cast<LlvmInstruction>())
                    if (user != instruction && !instruction.IsIdenticalTo(user)) return null;
                identicalUsers = true;
            }
            foreach (var operand in instruction.Operands)
            {
                if (operand == phi || operand is not LlvmInstruction definition) continue;
                if (definition is LlvmPhiInstruction && definition.Parent == phi.Parent) continue;
                if (DT.Dominates(definition, phi.Parent!)) continue;
                Count("phi.push.untranslatable_operand"); return null;
            }
            List<LlvmValue?> values = []; List<int> move = []; bool seenNonSimplified = false;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var input = phi.GetIncomingValue(i); var block = phi.GetIncomingBlock(i);
                Count("phi.push.incoming_visited");
                if (SimplifyInstructionWithPHI(instruction, phi, input, block) is { } simplified)
                { values.Add(simplified); Count("phi.push.incoming_simplified"); continue; }
                bool WillFold()
                {
                    if (input.SubclassId <= (byte)LlvmValueKind.ConstantDataLastVal || !input.HasOneUser) return false;
                    if (input is LlvmCallBase call && call.CalledFunction?.IntrinsicDescriptor?.HasComparisonRange == true &&
                        instruction is LlvmICmpInstruction { Right: LlvmConstantInt } cmp && cmp.Left == phi) return true;
                    return input is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast && cast.Operand.Type.IsIntOrIntVectorTy(1) &&
                        instruction is LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq, Right: LlvmConstantInt { IsZero: true } } compare && compare.Left == phi;
                }
                if (WillFold()) { move.Add(i); values.Add(null); Count("phi.push.will_fold"); continue; }
                if (!oneUse && !identicalUsers) return null;
                if (seenNonSimplified) { Count("phi.push.multiple_unsimplified"); return null; }
                seenNonSimplified = true;
                if (block.GetTerminator() is not LlvmUncondBrInstruction || !DT.IsReachableFromEntry(block)) return null;
                values.Add(null); move.Add(i);
                if (IsBackEdge(block, phi.Parent!)) { Count("phi.push.backedge"); return null; }
            }
            Dictionary<LlvmBasicBlock,LlvmInstruction> clones = [];
            foreach (int index in move)
            {
                var value = phi.GetIncomingValue(index); var block = phi.GetIncomingBlock(index);
                if (!clones.TryGetValue(block, out var clone))
                {
                    clone = instruction.Clone();
                    for (int op = 0; op < clone.OperandCount; ++op)
                    {
                        var old = clone.GetOperand(op)!;
                        clone.SetOperand(op, old == phi ? value : old.DoPHITranslation(phi.Parent!, block));
                    }
                    InsertNewInstBefore(clone, block.GetTerminator()); clones.Add(block, clone);
                    clone.DropUBImplyingAttrsAndMetadata(); Count("phi.push.clone_inserted");
                }
                values[index] = clone;
            }
            var result = new LlvmPhiInstruction(instruction.Type, phi.IncomingCount) { Source = phi.Source, LogicalType = instruction.LogicalType };
            InsertNewInstBefore(result, phi); result.TakeName(phi);
            for (int i = 0; i < phi.IncomingCount; ++i) result.AddIncoming(values[i]!, phi.GetIncomingBlock(i));
            if (identicalUsers)
            {
                var replace = phi.Users.Cast<LlvmInstruction>().Where(user => user != instruction).Distinct().ToArray();
                foreach (var user in replace) { ReplaceInstUsesWith(user, result); EraseInstFromFunction(user); }
            }
            Count("phi.push.operation_folded"); return ReplaceInstUsesWith(instruction, result);
        }
        private LlvmInstruction? FoldBinopWithPhiOperands(LlvmBinaryOperator binary)
        {
            if (FoldBinopWithRecurrence(binary) is { } recurrence) return recurrence;
            if (binary.Left is not LlvmPhiInstruction { HasOneUse: true } left || binary.Right is not LlvmPhiInstruction { HasOneUse: true } right ||
                left.IncomingCount != right.IncomingCount || binary.Parent != left.Parent || binary.Parent != right.Parent) return null;
            var identity = LlvmConstantFolding.BinOpIdentity(binary.Opcode, binary.Type, false);
            if (identity is not null)
            {
                List<LlvmValue> incoming = []; bool matches = true;
                for (int i = 0; i < left.IncomingCount; ++i)
                {
                    if (left.GetIncomingBlock(i) != right.GetIncomingBlock(i)) { matches = false; break; }
                    var a = left.GetIncomingValue(i); var b = right.GetIncomingValue(i);
                    if (a == identity) incoming.Add(b);
                    else if (b == identity) incoming.Add(a);
                    else { matches = false; break; }
                }
                if (matches)
                {
                    var result = new LlvmPhiInstruction(binary.Type, incoming.Count);
                    for (int i = 0; i < incoming.Count; ++i) result.AddIncoming(incoming[i], left.GetIncomingBlock(i));
                    Count("phi.binop.identity_pairs"); return result;
                }
            }
            if (left.IncomingCount != 2 || right.IncomingCount != 2) return null;
            int constantIndex = left.GetIncomingValue(0) is LlvmConstant ? 0 : left.GetIncomingValue(1) is LlvmConstant ? 1 : -1;
            if (constantIndex < 0) return null;
            var constantBlock = left.GetIncomingBlock(constantIndex); var other = left.GetIncomingBlock(1 - constantIndex);
            var c0 = (LlvmConstant)left.GetIncomingValue(constantIndex);
            int otherIndex = right.GetBasicBlockIndex(constantBlock);
            if (otherIndex < 0 || right.GetIncomingValue(otherIndex) is not LlvmConstant c1) return null;
            if (other.GetTerminator() is not LlvmUncondBrInstruction branch || !DT.IsReachableFromEntry(other)) return null;
            for (var instruction = binary.Parent!.FirstInstruction; instruction != binary; instruction = instruction!.Next)
                if (!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(instruction!, Metrics)) return null;
            var folded = LlvmConstantFolding.ConstantFoldBinaryOpOperands(binary.Opcode, c0, c1, DL, Metrics);
            if (folded is null) return null;
            Builder.SetInsertPoint(branch);
            var operation = Builder.CreateBinOp(binary.Opcode, left.GetIncomingValue(left.GetBasicBlockIndex(other)), right.GetIncomingValue(right.GetBasicBlockIndex(other)));
            if (operation is LlvmBinaryOperator instructionResult) instructionResult.CopyIRFlags(binary);
            var merged = new LlvmPhiInstruction(binary.Type, 2);
            merged.AddIncoming(operation, other); merged.AddIncoming(folded, constantBlock);
            Count("phi.binop.constant_pair"); return merged;
        }
    }
}
