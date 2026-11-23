using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private readonly record struct EdgeFixup(int PC, LlvmBasicBlock From, LlvmBasicBlock First, LlvmBasicBlock? Second = null);
        private readonly record struct Move(int To, int From, HpirWidth Width, SourceSpan? Source);
        private readonly Dictionary<HpirWidth, int> edgeScratch = [];

        private void LinkEdges()
        {
            foreach (var branch in branches)
            {
                int first = Edge(branch.From, branch.First);
                HpirInstruction instruction = code[branch.PC];
                code[branch.PC] = branch.Second is { } other
                    ? instruction with { B = first, D = Edge(branch.From, other) }
                    : instruction with { A = first };
            }
        }

        private int Edge(LlvmBasicBlock from, LlvmBasicBlock to)
        {
            if (edges.TryGetValue((from, to), out int existing)) return existing;
            List<Move> moves = [];
            for (var instruction = to.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                int incoming = phi.GetBasicBlockIndex(from);
                if (incoming < 0) throw Error("missing PHI predecessor", phi);
                var value = phi.GetIncomingValue(incoming);
                if (geometry.MixedTextResult(phi))
                    moves.Add(new(TextTag(phi), TextTag(value), HpirWidth.U8, phi.Source));
                if (FutureCell(phi))
                {
                    AddCarrierMoves(moves, phi, value, phi.Source);
                    continue;
                }
                if (UnionCell(phi))
                    moves.Add(new(UnionCompletion(phi), UnionCompletion(value), HpirWidth.U64, phi.Source));
                foreach (var leaf in geometry.Leaves(phi.Type))
                {
                    int dest = LeafLocation(phi, leaf.Offset), source = LeafLocation(value, leaf.Offset);
                    if (dest != source) moves.Add(new(dest, source, Width(leaf.Type), phi.Source));
                }
            }
            int target = blocks[to];
            if (moves.Count != 0)
            {
                int start = code.Count;
                phiMoves += moves.Count;
                phiCycles += EmitMoves(moves);
                Add(new(HpirOp.Jump, A: target));
                target = start;
            }
            edges.Add((from, to), target);
            return target;
        }

        private int EmitMoves(List<Move> moves)
        {
            int cycles = 0;
            while (moves.Count != 0)
            {
                int ready = FindReady(moves);
                if (ready >= 0)
                {
                    var move = moves[ready];
                    Copy(move.Width, move.To, move.From, move.Source);
                    moves.RemoveAt(ready);
                    continue;
                }

                // Preserve the old destination before overwriting a cycle.
                var first = moves[0];
                if (!edgeScratch.TryGetValue(first.Width, out int scratch))
                {
                    scratch = Reserve((int)first.Width, (int)first.Width);
                    edgeScratch.Add(first.Width, scratch);
                }
                Copy(first.Width, scratch, first.To, first.Source);
                for (int index = 0; index < moves.Count; ++index)
                    if (moves[index].From == first.To) moves[index] = moves[index] with { From = scratch };
                ++cycles;
            }
            return cycles;
        }

        private static int FindReady(List<Move> moves)
        {
            for (int index = 0; index < moves.Count; ++index)
            {
                int source = 0, destination = moves[index].To;
                while (source < moves.Count && moves[source].From != destination) ++source;
                if (source == moves.Count) return index;
            }
            return -1;
        }
    }
}
