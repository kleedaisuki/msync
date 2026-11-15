// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp SoleWriteToDeadLocal/tryToSinkInstruction.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool SoleWriteToDeadLocal(LlvmInstruction instruction)
        {
            if (instruction is not LlvmCallBase call) return false;
            if (LlvmMemoryLocation.GetForDest(call, TLI) is not { } destination ||
                LlvmValueTracking.GetUnderlyingObject(destination.Ptr!, metrics: Metrics) is not LlvmAllocaInstruction alloca) return false;
            List<LlvmUser> users = [];
            HashSet<LlvmUser> visited = [];
            PushUsers(alloca);
            while (users.Count != 0)
            {
                var user = (LlvmInstruction)users[^1];
                users.RemoveAt(users.Count - 1);
                if (user is LlvmGetElementPtrInstruction or LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast })
                { PushUsers(user); continue; }
                if (user == call) continue;
                return false;
            }
            return true;
            void PushUsers(LlvmInstruction value)
            {
                foreach (LlvmUser user in value.Users)
                    if (visited.Add(user)) users.Add(user);
            }
        }
        private bool TryToSinkInstruction(LlvmInstruction instruction, LlvmBasicBlock destination)
        {
            LlvmBasicBlock source = instruction.Parent!;
            if (instruction is LlvmPhiInstruction || instruction.IsEHPad || instruction.MayThrow() ||
                !instruction.WillReturn() || instruction.IsTerminator || instruction is LlvmAllocaInstruction)
            { Count("sink.retained.opcode-control"); return false; }
            if (instruction is LlvmCallInstruction { IsConvergent: true })
            { Count("sink.retained.convergent"); return false; }
            if (instruction.MayWriteToMemory() && !SoleWriteToDeadLocal(instruction))
            { Count("sink.retained.visible-write"); return false; }
            // There is no invariant_load metadata carrier in this scalar IR.
            if (instruction.MayReadFromMemory())
            {
                if (LlvmBasicBlockUtils.GetUniquePredecessor(destination) != source)
                { Count("sink.retained.read-not-immediate-successor"); return false; }
                for (LlvmInstruction? scan = instruction.Next; scan is not null; scan = scan.Next)
                    if (scan.MayWriteToMemory() && scan is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true })
                    { Count("sink.retained.intervening-write"); return false; }
            }
            List<LlvmCallInstruction> assumptions = [];
            instruction.DropDroppableUses(use =>
            {
                var user = (LlvmInstruction)use.User;
                if (user.Parent == destination) return false;
                if (user is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } assume && assume.Parent == source)
                { assumptions.Add(assume); return false; }
                Worklist.Add(user);
                return true;
            });
            LlvmInstruction insertion = FirstInsertionPoint(destination);
            instruction.RemoveFromParent();
            destination.InsertBefore(instruction, insertion);
            foreach (LlvmCallInstruction assume in assumptions)
            {
                assume.RemoveFromParent();
                destination.InsertBefore(assume, instruction.Next);
            }
            Count("sink.assumptions-moved", (ulong)assumptions.Count);
            return true;
        }
    }
}
