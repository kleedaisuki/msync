using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private HashSet<LlvmCallInstruction>? selfTails;
        private HpirParameter[]? tailParameters;
        private int tailMoves, tailCycles;

        private void PrepareSelfTails(HpirParameter[] parameters, CompilationTrace? trace)
        {
            if (!function.Users.Any(user => user is LlvmCallInstruction call && call.CalledFunction == function
                && call.Parent?.Parent == function)) return;
            // These are existing physical contracts, not escape or work
            // inference over decoded instructions. Unknown cases keep CALL/RET.
            if (geometry.IsEntry(function) || resultPointer >= 0 || textResultPointer >= 0 || geometry.FutureArguments(function)
                || function.Arguments.Any(geometry.TextParameter))
            { trace?.Metric("tail.self.rejected_abi", 1); return; }
            if (storage.Count != 0) { trace?.Metric("tail.self.rejected_frame_storage", 1); return; }

            var calls = new List<LlvmCallInstruction>();
            foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
            {
                if (instruction is LlvmCallInstruction { CalledFunction: { } target } call && target == function)
                {
                    calls.Add(call);
                    continue;
                }
                if (instruction is LlvmCallInstruction or LlvmFileInInst or LlvmFileOutInst or LlvmAllocaInstruction)
                {
                    trace?.Metric("tail.self.rejected_other_call_or_work", 1);
                    return;
                }
            }

            foreach (LlvmCallInstruction call in calls)
            {
                if (call.TailCallKind != LlvmTailCallKind.NoTail && call.Type == function.ReturnType && ForwardsReturn(call))
                    (selfTails ??= []).Add(call);
                else trace?.Metric("tail.self.rejected_continuation", 1);
            }
            if (selfTails is not null) tailParameters = parameters;
        }

        private bool ForwardsReturn(LlvmCallInstruction call)
        {
            // A suffix is elidable only if it observes nothing and forwards
            // this exact result along its specific predecessor edges.
            var from = call.Parent!;
            LlvmInstruction? next = call.Next;
            List<LlvmBasicBlock>? predecessors = null;
            for (int hops = 0; hops <= function.BasicBlocks.Count; hops++)
            {
                if (next is LlvmReturnInstruction ret)
                {
                    if (Result == HpirWidth.Void) return true;
                    LlvmValue? result = ret.ReturnValue;
                    for (int index = (predecessors?.Count ?? 0) - 1; index >= 0; index--)
                    {
                        if (result is LlvmPhiInstruction phi && phi.Parent == from)
                        {
                            int incoming = phi.GetBasicBlockIndex(predecessors![index]);
                            if (incoming < 0) return false;
                            result = phi.GetIncomingValue(incoming);
                        }
                        from = predecessors![index];
                    }
                    return result == call;
                }
                if (next is not LlvmUncondBrInstruction branch) return false;
                (predecessors ??= []).Add(from);
                from = branch.Destination;
                next = from.FirstInstruction;
                while (next is LlvmPhiInstruction) next = next.Next;
            }
            return false;
        }

        private bool IsSelfTail(LlvmInstruction instruction) =>
            selfTails is not null && instruction is LlvmCallInstruction call && selfTails.Contains(call);

        private void EmitSelfTail(LlvmCallInstruction call, IReadOnlyList<int> arguments)
        {
            HpirParameter[] parameters = tailParameters!;
            var moves = new List<Move>(parameters.Length);
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                int origin = arguments[index];
                if (origin == parameter.Offset) continue;
                if (origin < 0)
                {
                    // Snapshot an indirect argument before any rebinding
                    // can overwrite the physical word that addresses it.
                    int bytes = (int)parameter.Width;
                    int snapshot = Reserve(bytes, bytes);
                    Copy(parameter.Width, snapshot, origin, call.Source);
                    origin = snapshot;
                    ++tailMoves;
                }
                moves.Add(new(parameter.Offset, origin, parameter.Width, call.Source));
            }
            tailMoves += moves.Count;
            tailCycles += EmitMoves(moves);
            Add(new(HpirOp.Jump, A: blocks[function.BasicBlocks[0]]), call.Source);
        }

        private void ReportSelfTails(CompilationTrace? trace)
        {
            if (selfTails is null) return;
            trace?.Metric("tail.self.loop_sites", selfTails.Count);
            trace?.Metric("tail.self.argument_moves", tailMoves);
            trace?.Metric("tail.self.move_cycles", tailCycles);
        }
    }
}
