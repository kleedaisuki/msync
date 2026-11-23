using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private readonly List<(int PC, LlvmInstruction Site)> safepointInstructions = [];

        private HpirRootMap[] Roots(CompilationTrace? trace)
        {
            if (safepointInstructions.Count == 0) return [];
            var sites = safepointInstructions.ToLookup(pair => pair.Site, pair => pair.PC);
            var inputs = function.BasicBlocks.ToDictionary(block => block, _ => new HashSet<LlvmValue>());
            var live = new HashSet<LlvmValue>();
            int passes = 0, visits = 0;
            bool changed;
            do
            {
                changed = false;
                ++passes;
                for (int index = function.BasicBlocks.Count - 1; index >= 0; --index)
                {
                    var block = function.BasicBlocks[index];
                    ++visits;
                    ExitRoots(block, live, inputs);
                    ScanRoots(block, live);
                    if (inputs[block].SetEquals(live)) continue;
                    inputs[block].Clear();
                    inputs[block].UnionWith(live);
                    changed = true;
                }
            } while (changed);

            var maps = new List<HpirRootMap>(sites.Count);
            foreach (var block in function.BasicBlocks)
            {
                if (!block.Instructions.Any(sites.Contains)) continue;
                ExitRoots(block, live, inputs);
                ScanRoots(block, live, sites, maps);
            }
            trace?.Metric("rootmaps.blocks.visited", visits);
            trace?.Metric("rootmaps.dataflow_passes", passes);
            trace?.Metric("rootmaps.safepoints", maps.Count);
            trace?.Metric("rootmaps.base_roots", maps.Sum(map => map.RootOffsets.Length));
            trace?.Metric("rootmaps.borrowed_roots", maps.Sum(map => map.BorrowOffsets.Length));
            return maps.OrderBy(map => map.Instruction).ToArray();
        }

        private void ExitRoots(LlvmBasicBlock block, HashSet<LlvmValue> live,
            Dictionary<LlvmBasicBlock, HashSet<LlvmValue>> inputs)
        {
            live.Clear();
            if (block.LastInstruction is not { IsTerminator: true } terminal) return;
            for (int edge = 0; edge < terminal.SuccessorCount; ++edge)
            {
                var successor = terminal.GetSuccessor(edge);
                live.UnionWith(inputs[successor]);
                for (var instruction = successor.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
                {
                    int index = phi.GetBasicBlockIndex(block);
                    if (index >= 0) UseRoot(live, phi.GetIncomingValue(index));
                }
            }
        }

        private void ScanRoots(LlvmBasicBlock block, HashSet<LlvmValue> live,
            ILookup<LlvmInstruction, int>? sites = null, List<HpirRootMap>? maps = null)
        {
            for (var instruction = block.LastInstruction; instruction is not null; instruction = instruction.Previous)
            {
                live.Remove(instruction);
                if (instruction is LlvmPhiInstruction) continue;
                if (instruction is LlvmCallInstruction call)
                {
                    foreach (var argument in call.Arguments) UseRoot(live, argument);
                }
                else
                {
                    for (int index = 0; index < instruction.OperandCount; ++index)
                        UseRoot(live, instruction.GetOperand(index)!);
                }
                if (sites is not null)
                    foreach (int pc in sites[instruction]) maps!.Add(MakeRootMap(pc, live));
            }
        }

        private void UseRoot(HashSet<LlvmValue> live, LlvmValue value)
        {
            if (value is LlvmConstant) return;
            if (value is LlvmCallInstruction cursor
                && (numericCursors.ContainsKey(cursor) || liveTextCursors?.Contains(cursor) == true
                    || vectorCursors.TryGetValue(cursor, out var vector) && vector.Offset < 0)) return;
            if (value is LlvmCallInstruction format && deferredFormats?.TryGetValue(format, out var formatted) == true)
            {
                UseRoot(live, formatted);
                return;
            }
            if (FutureRoot(value) || UnionCell(value) || geometry.IsGuestPointer(value.Type) || Aggregate(value.Type)
                && geometry.Leaves(value.Type).Any(leaf => geometry.IsGuestPointer(leaf.Type)))
                live.Add(value);
        }

        private HpirRootMap MakeRootMap(int pc, HashSet<LlvmValue> live)
        {
            var exact = new SortedSet<int>();
            var borrowed = new SortedSet<int>();
            if (filterDemands is { } predicates && predicates.TryGetValue(pc, out int completion)) exact.Add(completion);
            // A materialization result is already owned by the physical loop
            // even though its logical call has not returned to LLVM users.
            if (transformBufferRoots.TryGetValue(pc, out var snapshot))
            {
                exact.Add(snapshot.Vector);
                if (snapshot.Item >= 0) exact.Add(snapshot.Item);
            }
            foreach (var original in live)
            {
                var value = RootOwner(original);
                if (value is LlvmConstant) continue;
                if (value is LlvmAllocaInstruction alloca)
                {
                    borrowed.UnionWith(frameRootOffsets[alloca]);
                    if (frameCompletions.TryGetValue(alloca, out int completionRoot)) exact.Add(completionRoot);
                }
                else if (geometry.MixedTextResult(value)) borrowed.Add(Location(value));
                else if (FutureRoot(value)) exact.Add(Location(value));
                else if (Aggregate(value.Type))
                {
                    if (UnionCell(value)) exact.Add(UnionCompletion(value));
                    foreach (var leaf in geometry.Leaves(value.Type))
                        if (geometry.IsGuestPointer(leaf.Type)) borrowed.Add(LeafLocation(value, leaf.Offset));
                }
                else if (value is LlvmHeapAllocInst || geometry.LiveTextPlan(value) is not null || geometry.DirectoryPlan(value) || geometry.IsNumericPlan(value)
                    || allocatedTransforms.Contains(value)) exact.Add(Location(value));
                else borrowed.Add(Location(value));
            }
            borrowed.ExceptWith(exact);
            return new(pc, exact.ToArray()) { BorrowOffsets = borrowed.ToArray() };
        }

        private LlvmValue RootOwner(LlvmValue value)
        {
            while (true)
            {
                switch (value)
                {
                    case LlvmCallInstruction call when vectorCursors.TryGetValue(call, out var cursor):
                        value = cursor.Owner;
                        break;
                    case LlvmCallInstruction call when slidePlans?.TryGetValue(call, out var slide) == true:
                        value = slide.Owner;
                        break;
                    case LlvmCallInstruction call when slideCursors?.TryGetValue(call, out var window) == true:
                        value = window.Owner;
                        break;
                    case LlvmCallInstruction call when directoryCursors is { } directories && directories.TryGetValue(call, out var directory):
                        value = directory;
                        break;
                    case LlvmCallInstruction call when filterCursors is { } filters && filters.TryGetValue(call, out var filter):
                        value = filter.Owner;
                        break;
                    case LlvmCallInstruction call when transformCursors.TryGetValue(call, out var transform):
                        value = transform.Owner;
                        break;
                    case LlvmCallInstruction call when sortedCursors.TryGetValue(call, out var sorted):
                        value = sorted.Owner;
                        break;
                    case LlvmCallInstruction call when concatCursors.TryGetValue(call, out var concat):
                        value = concat.Owner;
                        break;
                    case LlvmCallInstruction call when zipCursors.TryGetValue(call, out var zip):
                        value = zip.Owner;
                        break;
                    case LlvmCallInstruction call when countedCursors.TryGetValue(call, out var counted):
                        value = counted.Owner;
                        break;
                    case LlvmCallInstruction call when textPipelineCursors.TryGetValue(call, out var pipeline):
                        value = pipeline.Owner;
                        break;
                    case LlvmCallInstruction call when geometry.IsUniformProjection(call.CalledFunction):
                        value = call.GetArgument(0);
                        break;
                    case LlvmGetElementPtrInstruction gep: value = gep.PointerOperand; break;
                    case LlvmCastInstruction cast when cast.Operand.Type.IsPointerTy(): value = cast.Operand; break;
                    default: return value;
                }
            }
        }
    }
}
