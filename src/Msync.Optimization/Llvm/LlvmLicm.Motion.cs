// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LICM.cpp eraseInstruction / moveInstructionBefore / hoist /
// cloneInstructionInExitBlock / sinkThroughTriviallyReplaceablePHI.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private void EraseInstruction(LlvmInstruction instruction)
        {
            Updater.RemoveMemoryAccess(instruction);
            Safety.RemoveInstruction(instruction);
            instruction.EraseFromParent();
            Count("instructions.erased");
        }

        private void MoveInstructionBefore(LlvmInstruction instruction, LlvmInstruction before)
        {
            LlvmBasicBlock destination = before.Parent!;
            Safety.RemoveInstruction(instruction);
            Safety.InsertInstructionTo(instruction, destination);
            instruction.RemoveFromParent();
            destination.InsertBefore(instruction, before);
            if (Memory.GetMemoryAccess(instruction) is { } access)
                Updater.MoveToPlace(access, destination, LlvmMemorySsaInsertionPlace.BeforeTerminator);
            Count("instructions.moved");
        }

        private void Hoist(LlvmInstruction instruction, LlvmBasicBlock destination)
        {
            if (Trace is null) HoistCore(instruction, destination);
            else Trace.Run("optimizer.llvm-licm.hoist", (Work: this, instruction, destination),
                static state => state.Work.HoistCore(state.instruction, state.destination), InstructionAttributes(instruction));
        }

        private void HoistCore(LlvmInstruction instruction, LlvmBasicBlock destination)
        {
            // Current calls carry no UB-implying parameter attributes. Arithmetic
            // poison flags are not metadata and must remain on moved instructions.
            LlvmInstruction before = instruction is LlvmPhiInstruction ? FirstNonPhi(destination) : destination.GetTerminator();
            MoveInstructionBefore(instruction, before);
            Count("instructions.hoisted");
            if (instruction is LlvmLoadInstruction) Count("loads.moved");
            else if (instruction is LlvmCallInstruction) Count("calls.moved");
        }

        private LlvmInstruction CloneInExit(LlvmInstruction instruction, LlvmBasicBlock exit, LlvmPhiInstruction phi)
        {
            // No funclet pads or funclet colors are represented. Clone preserves
            // ordinary source-language ABI operand bundles and per-use views.
            LlvmInstruction clone = instruction.Clone();
            exit.InsertBefore(clone, FirstInsertion(exit));
            if (instruction.HasName) clone.SetName(AppendName(instruction.NameBytes.Span, ".le"u8));
            if (Memory.GetMemoryAccess(instruction) is not null)
            {
                var memory = Updater.CreateMemoryAccessInBB(clone, null, exit, LlvmMemorySsaInsertionPlace.Beginning,
                    creationMustSucceed: false);
                if (memory is LlvmMemoryDef definition) Updater.InsertDef(definition, true);
                else if (memory is LlvmMemoryUse use) Updater.InsertUse(use, true);
            }
            foreach (LlvmUse operand in clone.OperandUses)
            {
                if (!LoopInfo.WouldBeOutOfLoopUseRequiringLCSSA(operand.Value!, phi.Parent!)) continue;
                var producer = (LlvmInstruction)operand.Value!;
                var incoming = new LlvmPhiInstruction(producer.Type, phi.IncomingCount)
                { LogicalType = operand.LogicalType >= 0 ? operand.LogicalType : producer.LogicalType, Source = producer.Source };
                incoming.SetName(AppendName(producer.NameBytes.Span, ".lcssa"u8));
                exit.InsertBefore(incoming, exit.FirstInstruction);
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    incoming.AddIncoming(producer, phi.GetIncomingBlock(index));
                    incoming.GetOperandUse(index).LogicalType = operand.LogicalType;
                }
                operand.Set(incoming);
                Count("sink.operand_lcssa_phis");
            }
            Count("sink.clones");
            return clone;
        }

        private LlvmInstruction SinkThroughPhi(LlvmPhiInstruction phi, LlvmInstruction instruction,
            Dictionary<LlvmBasicBlock, LlvmInstruction> copies)
        {
            LlvmBasicBlock exit = phi.Parent!;
            if (copies.TryGetValue(exit, out var copy)) return copy;
            copy = CloneInExit(instruction, exit, phi);
            copies.Add(exit, copy);
            return copy;
        }

        private static bool TriviallyReplaceablePhi(LlvmPhiInstruction phi, LlvmInstruction instruction)
        {
            for (int index = 0; index < phi.IncomingCount; ++index)
                if (phi.GetIncomingValue(index) != instruction) return false;
            return true;
        }

        private static LlvmInstruction FirstNonPhi(LlvmBasicBlock block)
        {
            LlvmInstruction? instruction = block.FirstInstruction;
            while (instruction is LlvmPhiInstruction) instruction = instruction.Next;
            return instruction!;
        }

        private static byte[] AppendName(ReadOnlySpan<byte> name, ReadOnlySpan<byte> suffix)
        {
            byte[] bytes = new byte[name.Length + suffix.Length];
            name.CopyTo(bytes); suffix.CopyTo(bytes.AsSpan(name.Length));
            return bytes;
        }

        private Dictionary<string, string> InstructionAttributes(LlvmInstruction instruction)
        {
            var attributes = Attributes(Loop.Header);
            attributes["instruction.block"] = instruction.Parent!.Name;
            attributes["instruction.opcode"] = instruction.Opcode.ToString();
            if (instruction.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
