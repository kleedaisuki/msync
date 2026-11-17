using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

// The binder only queries membership. Solve on basic blocks, then expand each
// block once; no per-instruction sets, copies, or work-list entries are needed.
internal sealed class NativeBackendLiveness(int words, ulong[] bits)
{
    internal bool ContainsAfter(int pc, int slot) =>
        slot >= 0 && (bits[pc * words + (slot >> 6)] & (1UL << (slot & 63))) != 0;

    internal static NativeBackendLiveness Analyze(NativeInstruction[] metadata, int slots,
        NativeControlFlow flow, CompilationTrace? trace) => trace is null
        ? new Solver(metadata, slots, flow).Run(null)
        : trace.MeasureDetailed("backend.lower.liveness", (metadata, slots, flow, trace),
            static state => new Solver(state.metadata, state.slots, state.flow).Run(state.trace));

    private sealed class Solver(NativeInstruction[] code, int slots, NativeControlFlow flow)
    {
        private readonly int slotCount = slots;
        private readonly int words = (slots + 63) / 64;

        internal NativeBackendLiveness Run(CompilationTrace? trace)
        {
            int cells = flow.Blocks.Count * words;
            ulong[] use = new ulong[cells];
            ulong[] kill = new ulong[cells];
            ulong[] entry = new ulong[cells];
            ulong[] scratch = new ulong[words];
            ulong[] after = new ulong[code.Length * words];
            if (trace is null)
            {
                BuildTransfers(use, kill);
                Solve(use, kill, entry, scratch, null);
                Expand(entry, scratch, after);
            }
            else
            {
                trace.Run("backend.liveness.transfer", (Solver: this, use, kill),
                    static state => state.Solver.BuildTransfers(state.use, state.kill));
                trace.Run("backend.liveness.solve", (Solver: this, use, kill, entry, scratch, trace),
                    static state => state.Solver.Solve(state.use, state.kill, state.entry, state.scratch, state.trace));
                trace.Run("backend.liveness.expand", (Solver: this, entry, scratch, after),
                    static state => state.Solver.Expand(state.entry, state.scratch, state.after));
                trace.Metric("blocks", flow.Blocks.Count);
                trace.Metric("instructions", code.Length);
                trace.Metric("slots", slotCount);
                trace.Metric("bitset.words", words);
                trace.Metric("after.bytes", after.LongLength * sizeof(ulong));
            }
            return new(words, after);
        }

        private void BuildTransfers(ulong[] use, ulong[] kill)
        {
            for (int id = 0; id < flow.Blocks.Count; id++)
            {
                NativeControlFlow.Block block = flow.Blocks[id];
                Span<ulong> need = Slice(use, id), written = Slice(kill, id);
                for (int pc = block.End - 1; pc >= block.Start; pc--)
                {
                    foreach (int slot in NativeInstructionFacts.Definitions(code[pc]))
                    {
                        Set(written, slot);
                        Clear(need, slot);
                    }
                    foreach (int slot in NativeInstructionFacts.Reads(code[pc])) Set(need, slot);
                }
            }
        }

        private void Solve(ulong[] use, ulong[] kill, ulong[] entry, ulong[] scratch, CompilationTrace? trace)
        {
            int count = flow.Blocks.Count;
            List<int>?[] predecessors = new List<int>?[count];
            for (int id = 0; id < count; id++)
                foreach (int successor in flow.Blocks[id].Successors)
                    (predecessors[successor] ??= []).Add(id);
            Queue<int> work = new(count);
            bool[] queued = new bool[count];
            for (int id = count - 1; id >= 0; id--)
            {
                work.Enqueue(id);
                queued[id] = true;
            }
            long visits = 0, requeues = 0, edges = 0;
            while (work.TryDequeue(out int id))
            {
                queued[id] = false;
                NativeControlFlow.Block block = flow.Blocks[id];
                UnionSuccessors(block, entry, scratch);
                if (trace is not null) { visits++; edges += block.Successors.Length; }
                if (!UpdateEntry(Slice(use, id), Slice(kill, id), Slice(entry, id), scratch)) continue;
                if (predecessors[id] is not { } previous) continue;
                foreach (int predecessor in previous)
                {
                    if (queued[predecessor]) continue;
                    queued[predecessor] = true;
                    work.Enqueue(predecessor);
                    if (trace is not null) requeues++;
                }
            }
            trace?.Metric("block.visits", visits);
            trace?.Metric("block.requeues", requeues);
            trace?.Metric("successor.visits", edges);
        }

        private static bool UpdateEntry(ReadOnlySpan<ulong> use, ReadOnlySpan<ulong> kill, Span<ulong> entry,
            ReadOnlySpan<ulong> next)
        {
            ulong changed = 0;
            for (int i = 0; i < entry.Length; i++)
            {
                ulong word = use[i] | (next[i] & ~kill[i]);
                changed |= word ^ entry[i];
                entry[i] = word;
            }
            return changed != 0;
        }

        private void Expand(ulong[] entry, ulong[] scratch, ulong[] after)
        {
            foreach (NativeControlFlow.Block block in flow.Blocks)
            {
                UnionSuccessors(block, entry, scratch);
                for (int pc = block.End - 1; pc >= block.Start; pc--)
                {
                    scratch.AsSpan().CopyTo(Slice(after, pc));
                    foreach (int slot in NativeInstructionFacts.Definitions(code[pc])) Clear(scratch, slot);
                    foreach (int slot in NativeInstructionFacts.Reads(code[pc])) Set(scratch, slot);
                }
            }
        }

        private void UnionSuccessors(NativeControlFlow.Block block, ulong[] entry, Span<ulong> result)
        {
            result.Clear();
            foreach (int successor in block.Successors)
            {
                Span<ulong> source = Slice(entry, successor);
                for (int i = 0; i < result.Length; i++) result[i] |= source[i];
            }
        }

        private Span<ulong> Slice(ulong[] table, int row) => table.AsSpan(row * words, words);
        private static void Set(Span<ulong> set, int slot) => set[slot >> 6] |= 1UL << (slot & 63);
        private static void Clear(Span<ulong> set, int slot) => set[slot >> 6] &= ~(1UL << (slot & 63));
    }
}
