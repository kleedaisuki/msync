// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// PHI copy construction adapted from LLVM a55e64432a516fd6507d3ecfba7407cfe425f940
// llvm/lib/CodeGen/PHIElimination.cpp LowerPHINode; see vendor/llvm-reference/llvm/LICENSE.TXT.
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private int phiCarrierAdaptations;

        private void PhiCopy(int target, int source, Msync.Core.SourceSpan? origin)
        {
            int type = slots[target].Type;
            if (!owner.Types[type].PhysicalType.Equals(owner.Types[slots[source].Type].PhysicalType))
            {
                type = -1;
                ++phiCarrierAdaptations;
            }
            Add(new(NativeOp.Copy, target, source, Type: type, Source: origin));
        }

        // LLVM PHIElimination.cpp LowerPHINode: a fresh IncomingReg is assigned in
        // each predecessor, then copied into the SSA destination at block entry.
        private void PreparePhis()
        {
            foreach (var block in function.BasicBlocks)
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is not LlvmPhiInstruction phi) break;
                    PreparePhi(phi);
                }
        }
        private void PreparePhi(LlvmPhiInstruction phi)
        {
            if (TryPrepareAggregatePhi(phi)) return;
            int temporary = Register(SelectedValueType(phi));
            incoming.Add(phi, temporary);
            HashSet<LlvmBasicBlock> inserted = [];
            for (int i = phi.IncomingCount - 1; i >= 0; --i)
            {
                var predecessor = phi.GetIncomingBlock(i);
                if (!inserted.Add(predecessor)) continue;
                if (!phiSources.TryGetValue(predecessor, out var copies))
                    phiSources.Add(predecessor, copies = []);
                copies.Add((temporary, phi.GetOperandUse(i), phi.Source));
            }
        }

        private void EmitPhiSources(LlvmBasicBlock block)
        {
            EmitAggregatePhiSources(block);
            if (!phiSources.TryGetValue(block, out var copies)) return;
            foreach (var copy in copies)
                PhiCopy(copy.Target, Slot(copy.Value, slots[copy.Target].Type), copy.Source);
        }
        private void Jump(LlvmBasicBlock destination, Msync.Core.SourceSpan? source)
        {
            branches.Add((code.Count, destination, null));
            Add(new(NativeOp.Jump, Source: source));
        }
        private void Branch(LlvmCondBrInstruction branch)
        {
            branches.Add((code.Count, branch.GetSuccessor(0), branch.GetSuccessor(1)));
            Add(new(NativeOp.Branch, A: OperandSlot(branch, 0, owner.Primitive(Msync.Core.TypeKind.Bool)), Source: branch.Source));
        }
        private void Emit(LlvmInstruction instruction, LlvmBasicBlock block)
        {
            instructionSource = instruction.Source;
            if (instruction.IsTerminator) EmitPhiSources(block);
            switch (instruction)
            {
                case LlvmPhiInstruction phi:
                    if (TryEmitAggregatePhi(phi)) break;
                    PhiCopy(Slot(phi), incoming[phi], phi.Source);
                    break;
                case LlvmUncondBrInstruction branch: Jump(branch.Destination, instruction.Source); break;
                case LlvmCondBrInstruction branch: Branch(branch); break;
                case LlvmReturnInstruction result:
                    Add(new(NativeOp.Return, A: result.ReturnValue is not null
                        ? OperandSlot(result, 0, ResultType()) : Null(), Source: instruction.Source));
                    break;
                case LlvmUnreachableInstruction: Add(new(NativeOp.Unreachable, Source: instruction.Source)); break;
                case LlvmAllocaInstruction allocation: Alloca(allocation); break;
                case LlvmHeapAllocInst allocation: Heap(allocation); break;
                case LlvmGetElementPtrInstruction address: FieldAddress(address); break;
                case LlvmLoadInstruction load: Load(load); break;
                case LlvmStoreInstruction store:
                    if (StaticStore(store)) break;
                    int pointee = MemoryStorageType(store.GetOperandUse(1), store.ValueOperand);
                    Add(new(NativeOp.Store, A: MemoryAddressSlot(store, 1, pointee), B: OperandSlot(store, 0, pointee), C: 0,
                        Type: pointee, Source: store.Source));
                    break;
                case LlvmCastInstruction cast: MachineCast(cast); break;
                case LlvmUnaryOperator machineUnary when !machineUnary.Type.IsDoubleTy():
                    MachineUnary(machineUnary); break;
                case LlvmUnaryOperator unary:
                    Add(new(NativeOp.Unary, Slot(unary), OperandSlot(unary, 0), Immediate: (int)NativeUnary.Negate, Source: unary.Source));
                    break;
                case LlvmBinaryOperator binary: Binary(binary); break;
                case LlvmCmpInstruction comparison: Compare(comparison); break;
                case LlvmSelectInstruction select: Select(select); break;
                case LlvmFileInInst input: FileIn(input); break;
                case LlvmFileOutInst output: FileOut(output); break;
                case LlvmCallInstruction call:
                    if (!TryAggregateIntrinsic(call)) Call(call);
                    break;
                case LlvmInsertValueInstruction aggregate: FunctionAggregate(aggregate); break;
                case LlvmExtractValueInstruction extract: ExtractFunction(extract); break;
                default: throw Unsupported(instruction, instruction.Opcode.ToString());
            }
            DefineAggregateBoundary(instruction);
        }
        private int Null()
        {
            int slot = Register(owner.Primitive(Msync.Core.TypeKind.Null));
            prelude.Add(new(NativeOp.Constant, slot, owner.Constant(new(NativeKind.Null))));
            return slot;
        }
    }
}
