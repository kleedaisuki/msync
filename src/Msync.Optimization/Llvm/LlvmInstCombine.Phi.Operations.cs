// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombinePHI.cpp 38–78, 356–957.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private void PHIArgMergedDebugLoc(LlvmInstruction instruction, LlvmPhiInstruction phi)
        {
            // SourceSpan has no LLVM DILocation merge carrier. Equal source locations survive;
            // conflicting locations are dropped rather than attributing the merge to one arm.
            instruction.Source = ((LlvmInstruction)phi.GetIncomingValue(0)).Source;
            for (int i = 1; i < phi.IncomingCount; ++i)
                if (instruction.Source != ((LlvmInstruction)phi.GetIncomingValue(i)).Source) instruction.Source = null;
        }
        private static IEnumerable<LlvmPhiInstruction> PhiNodes(LlvmBasicBlock block) => block.Instructions.OfType<LlvmPhiInstruction>();
        private static LlvmInstruction? PhiInsertionPoint(LlvmBasicBlock block) =>
            block.Instructions.FirstOrDefault(instruction => instruction is not LlvmPhiInstruction && !instruction.IsEHPad);
        private bool FoldDeadPhiWeb(LlvmPhiInstruction phi)
        {
            List<LlvmPhiInstruction> stack = [phi]; HashSet<LlvmPhiInstruction> visited = [phi];
            while (stack.Count != 0)
            {
                var item = stack[^1]; stack.RemoveAt(stack.Count - 1); Count("phi.dead_web.work");
                foreach (var user in item.Users)
                {
                    if (user is not LlvmPhiInstruction next) return false;
                    if (!visited.Add(next)) continue;
                    if (visited.Count >= 16) { Count("phi.dead_web.limit"); return false; }
                    stack.Add(next);
                }
            }
            foreach (var item in visited) ReplaceInstUsesWith(item, LlvmPoisonValue.Get(item.Type));
            foreach (var item in visited) EraseInstFromFunction(item);
            Count("phi.dead_web.removed", (ulong)visited.Count); return true;
        }
        private static int PhiOperandLogicalType(LlvmInstruction instruction, int operand)
        {
            int role = instruction.GetOperandUse(operand).LogicalType;
            return role >= 0 ? role : instruction.GetOperand(operand)!.LogicalType;
        }
        private LlvmPhiInstruction PhiOperand(LlvmPhiInstruction phi, int operand, LlvmInstruction first)
        {
            var result = new LlvmPhiInstruction(first.GetOperand(operand)!.Type, phi.IncomingCount)
            { LogicalType = PhiOperandLogicalType(first, operand) };
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var incoming = (LlvmInstruction)phi.GetIncomingValue(i);
                Count("phi.operand_incoming_visited");
                result.AddIncoming(incoming.GetOperand(operand)!, phi.GetIncomingBlock(i));
                result.GetOperandUse(i).LogicalType = incoming.GetOperandUse(operand).LogicalType;
            }
            result.SetName(first.GetOperand(operand)!.Name + ".pn");
            InsertNewInstBefore(result, phi); Count("phi.operand_phi_created"); return result;
        }
        private LlvmInstruction? FoldPHIArgInsertValueInstructionIntoPHI(LlvmPhiInstruction phi)
        {
            var first = (LlvmInsertValueInstruction)phi.GetIncomingValue(0);
            for (int i = 1; i < phi.IncomingCount; ++i)
                if (phi.GetIncomingValue(i) is not LlvmInsertValueInstruction item || !item.HasOneUser || !item.Indices.SequenceEqual(first.Indices)) return null;
            var aggregate = PhiOperand(phi, 0, first); var value = PhiOperand(phi, 1, first);
            var result = new LlvmInsertValueInstruction(aggregate, value, first.Indices);
            PHIArgMergedDebugLoc(result, phi); Count("phi.insertvalue_merged"); return result;
        }
        private LlvmInstruction? FoldPHIArgExtractValueInstructionIntoPHI(LlvmPhiInstruction phi)
        {
            var first = (LlvmExtractValueInstruction)phi.GetIncomingValue(0);
            for (int i = 1; i < phi.IncomingCount; ++i)
                if (phi.GetIncomingValue(i) is not LlvmExtractValueInstruction item || !item.HasOneUser ||
                    !item.Indices.SequenceEqual(first.Indices) || item.AggregateOperand.Type != first.AggregateOperand.Type) return null;
            var result = new LlvmExtractValueInstruction(PhiOperand(phi, 0, first), first.Indices);
            PHIArgMergedDebugLoc(result, phi); Count("phi.extractvalue_merged"); return result;
        }
        private LlvmInstruction? FoldPHIArgBinOpIntoPHI(LlvmPhiInstruction phi)
        {
            var first = (LlvmInstruction)phi.GetIncomingValue(0);
            LlvmValue? left = first.GetOperand(0)!, right = first.GetOperand(1)!;
            var leftType = left.Type; var rightType = right.Type;
            for (int i = 1; i < phi.IncomingCount; ++i)
            {
                if (phi.GetIncomingValue(i) is not LlvmInstruction item || item.Opcode != first.Opcode || !item.HasOneUser ||
                    item.GetOperand(0)!.Type != leftType || item.GetOperand(1)!.Type != rightType) return null;
                if (item is LlvmCmpInstruction cmp && cmp.Predicate != ((LlvmCmpInstruction)first).Predicate) return null;
                if (item.GetOperand(0) != left) left = null;
                if (item.GetOperand(1) != right) right = null;
            }
            if (left is null && right is null) { Count("phi.binop.two_phis_required"); return null; }
            left ??= PhiOperand(phi, 0, first); right ??= PhiOperand(phi, 1, first);
            LlvmInstruction result;
            if (first is LlvmCmpInstruction compare)
                result = compare is LlvmICmpInstruction ? new LlvmICmpInstruction(compare.Predicate, left, right) : new LlvmFCmpInstruction(compare.Predicate, left, right);
            else
            {
                result = first.Clone(); result.SetOperand(0, left); result.SetOperand(1, right);
                for (int i = 1; i < phi.IncomingCount; ++i) result.AndIRFlags(phi.GetIncomingValue(i));
            }
            PHIArgMergedDebugLoc(result, phi); Count("phi.binop_merged"); return result;
        }
        private LlvmInstruction? FoldPHIArgGEPIntoPHI(LlvmPhiInstruction phi)
        {
            var first = (LlvmGetElementPtrInstruction)phi.GetIncomingValue(0);
            LlvmValue?[] operands = first.Operands.ToArray();
            bool allAllocas = true, neededPhi = false;
            var flags = first.NoWrapFlags;
            for (int i = 1; i < phi.IncomingCount; ++i)
            {
                if (phi.GetIncomingValue(i) is not LlvmGetElementPtrInstruction gep || !gep.HasOneUser ||
                    gep.SourceElementType != first.SourceElementType || gep.OperandCount != first.OperandCount) return null;
                if (gep.GetTargetStorageType() != first.GetTargetStorageType())
                { Count("phi.gep_retained_distinct_storage"); return null; }
                Count("phi.gep.incoming_visited");
                flags &= gep.NoWrapFlags;
                if (allAllocas && (gep.PointerOperand is not LlvmAllocaInstruction || !gep.Indices.All(index => index is LlvmConstantInt))) allAllocas = false;
                for (int op = 0; op < first.OperandCount; ++op)
                {
                    if (first.GetOperand(op) == gep.GetOperand(op)) continue;
                    if (op != 0 && gep.GetTargetStorageType() is not null)
                    { Count("phi.gep_retained_typed_index"); return null; }
                    if (first.GetOperand(op) is LlvmConstant || gep.GetOperand(op) is LlvmConstant ||
                        first.GetOperand(op)!.Type != gep.GetOperand(op)!.Type || neededPhi) return null;
                    operands[op] = null; neededPhi = true;
                }
            }
            if (allAllocas) { Count("phi.gep.alloca_addresses"); return null; }
            for (int op = 0; op < operands.Length; ++op) operands[op] ??= PhiOperand(phi, op, first);
            var result = new LlvmGetElementPtrInstruction(first.SourceElementType, operands[0]!, operands.Skip(1).Select(value => value!).ToArray(), flags);
            result.TargetStorageType = first.GetTargetStorageType();
            PHIArgMergedDebugLoc(result, phi); Count("phi.gep_merged"); return result;
        }
        private bool IsSafeAndProfitableToSinkLoad(LlvmLoadInstruction load)
        {
            for (var instruction = load.Next; instruction is not null; instruction = instruction.Next)
            {
                Count("phi.load_sink.memory_scan");
                if (!instruction.MayWriteToMemory()) continue;
                if (instruction is LlvmCallBase call && (call.GetMemoryEffects().Raw & ~12U) == 0) continue;
                return false;
            }
            if (load.PointerOperand is LlvmAllocaInstruction alloca)
            {
                bool taken = alloca.Users.Any(user => user is not LlvmLoadInstruction &&
                    !(user is LlvmStoreInstruction store && store.PointerOperand == alloca));
                if (!taken && alloca.IsStaticAlloca) return false;
            }
            if (load.PointerOperand is LlvmGetElementPtrInstruction { PointerOperand: LlvmAllocaInstruction { IsStaticAlloca: true } } gep &&
                gep.Indices.All(index => index is LlvmConstantInt)) return false;
            return true;
        }
        private LlvmInstruction? FoldPHIArgLoadIntoPHI(LlvmPhiInstruction phi)
        {
            var first = (LlvmLoadInstruction)phi.GetIncomingValue(0);
            if (!LlvmLocal.CanReplaceOperandWithVariable(first, 0) || !first.IsSimple ||
                first.Parent != phi.GetIncomingBlock(0) || !IsSafeAndProfitableToSinkLoad(first)) return null;
            var alignment = first.Alignment; var addressSpace = ((LlvmPointerType)first.PointerOperand.Type).AddressSpace;
            for (int i = 1; i < phi.IncomingCount; ++i)
            {
                if (phi.GetIncomingValue(i) is not LlvmLoadInstruction item || !item.HasOneUser || !item.IsSimple ||
                    ((LlvmPointerType)item.PointerOperand.Type).AddressSpace != addressSpace || !LlvmLocal.CanReplaceOperandWithVariable(item, 0) ||
                    item.Parent != phi.GetIncomingBlock(i) || !IsSafeAndProfitableToSinkLoad(item)) return null;
                if (first.PointerOperand != item.PointerOperand && (SplitStoragePointerLoad(first) || SplitStoragePointerLoad(item)))
                { Count("phi.load_retained_split_storage"); return null; }
                Count("phi.load.incoming_visited");
                if (item.Alignment.Value < alignment.Value) alignment = item.Alignment;
            }
            var pointerPhi = new LlvmPhiInstruction(first.PointerOperand.Type, phi.IncomingCount)
            { LogicalType = PhiOperandLogicalType(first, 0) };
            LlvmValue? common = first.PointerOperand;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var load = (LlvmLoadInstruction)phi.GetIncomingValue(i);
                pointerPhi.AddIncoming(load.PointerOperand, phi.GetIncomingBlock(i));
                pointerPhi.GetOperandUse(i).LogicalType = load.GetOperandUse(0).LogicalType;
                if (load.PointerOperand != common) common = null;
            }
            var result = new LlvmLoadInstruction(first.Type, common ?? pointerPhi, alignment);
            if (common is null) InsertNewInstBefore(pointerPhi, phi); else pointerPhi.DropAllReferences();
            PHIArgMergedDebugLoc(result, phi); Count("phi.load_merged"); return result;
        }

        // LLVM aggregate expansion loads function components as physical pointers.
        // The target recovers each component from its containing logical field;
        // an address phi would first require a standalone pointer to that component.
        private static bool SplitStoragePointerLoad(LlvmLoadInstruction load) =>
            load.LogicalType < 0 && load.Type.IsPointerTy() &&
            (load.GetOperandUse(0).TargetStorageType is not null ||
                load.PointerOperand is LlvmGetElementPtrInstruction gep && gep.GetTargetStorageType() is not null);
        private LlvmInstruction? FoldPHIArgOpIntoPHI(LlvmPhiInstruction phi)
        {
            if (phi.Parent!.GetTerminator().IsEHPad) return null;
            var first = (LlvmInstruction)phi.GetIncomingValue(0);
            if (first is LlvmGetElementPtrInstruction) return FoldPHIArgGEPIntoPHI(phi);
            if (first is LlvmLoadInstruction) return FoldPHIArgLoadIntoPHI(phi);
            if (first is LlvmInsertValueInstruction) return FoldPHIArgInsertValueInstructionIntoPHI(phi);
            if (first is LlvmExtractValueInstruction) return FoldPHIArgExtractValueInstructionIntoPHI(phi);
            LlvmConstant? constant = null; LlvmType? castType = null;
            if (first is LlvmCastInstruction cast)
            {
                Count("phi.operation.cast_candidate");
                castType = cast.Operand.Type;
                if (phi.Type.IsIntegerTy() && castType.IsIntegerTy() && !ShouldChangeType(phi.Type, castType)) return null;
            }
            else if (first is LlvmBinaryOperator or LlvmCmpInstruction)
            {
                constant = first.GetOperand(1) as LlvmConstant;
                if (constant is null) return FoldPHIArgBinOpIntoPHI(phi);
            }
            else return null;
            for (int i = 1; i < phi.IncomingCount; ++i)
            {
                if (phi.GetIncomingValue(i) is not LlvmInstruction item || !item.HasOneUser || !item.IsSameOperationAs(first)) return null;
                if (castType is not null ? item.GetOperand(0)!.Type != castType : item.GetOperand(1) != constant) return null;
            }
            LlvmValue input = first.GetOperand(0)!;
            bool allSame = Enumerable.Range(1, phi.IncomingCount - 1).All(i => ((LlvmInstruction)phi.GetIncomingValue(i)).GetOperand(0) == input);
            if (!allSame) input = PhiOperand(phi, 0, first);
            LlvmInstruction result;
            if (first is LlvmCastInstruction firstCast) result = new LlvmCastInstruction(firstCast.Opcode, input, phi.Type);
            else if (first is LlvmBinaryOperator)
            {
                result = first.Clone(); result.SetOperand(0, input);
                for (int i = 1; i < phi.IncomingCount; ++i) result.AndIRFlags(phi.GetIncomingValue(i));
            }
            else
            {
                var compare = (LlvmCmpInstruction)first;
                result = compare is LlvmICmpInstruction ? new LlvmICmpInstruction(compare.Predicate, input, constant!) : new LlvmFCmpInstruction(compare.Predicate, input, constant!);
            }
            PHIArgMergedDebugLoc(result, phi); Count("phi.operation_merged"); return result;
        }
    }
}
