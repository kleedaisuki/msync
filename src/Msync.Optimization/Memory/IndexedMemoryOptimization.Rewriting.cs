namespace Msync.Optimization;

internal static partial class IndexedMemoryOptimization
{
    private sealed partial class Analysis
    {
        private FunctionCode Rewrite()
        {
            int localCount = function.LocalCount;
            Dictionary<Read, int> slots = new(ReferenceEqualityComparer.Instance);
            Dictionary<int, List<int>> retained = [];
            Dictionary<ControlFlowEdge, List<(int From, int To)>> copies = [];
            foreach (var replacement in replacements.Values) Slot(replacement);

            int[] positions = new int[code.Count + 1];
            List<Instruction> output = [];
            List<(int Position, int Target)> branches = [];
            for (int pc = 0; pc < code.Count; pc++)
            {
                positions[pc] = output.Count;
                Instruction instruction = code[pc];
                if (replacements.TryGetValue(pc, out var source))
                {
                    for (int argument = 0; argument < instruction.A; argument++) output.Add(new(OpCode.Pop, Span: instruction.Span));
                    output.Add(new(OpCode.LoadStorage, Slot(source), Span: instruction.Span));
                }
                else
                {
                    if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext)
                        EmitBranch(pc, instruction);
                    else output.Add(instruction);
                    if (retained.TryGetValue(pc, out var saves))
                        foreach (int slot in saves)
                        {
                            output.Add(new(OpCode.Dup, Span: instruction.Span));
                            output.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
                        }
                }
                int block = graph.BlockAt(pc);
                if (pc + 1 != graph.Blocks[block].End) continue;
                foreach (var edge in graph.OutgoingEdges(block))
                    if ((edge.Kind is ControlFlowEdgeKind.Fallthrough or ControlFlowEdgeKind.IteratorYield)
                        && copies.TryGetValue(edge, out var fallthrough))
                        EmitCopies(fallthrough, instruction.Span);
            }
            positions[^1] = output.Count;
            foreach (var (position, target) in branches)
                output[position] = WithTarget(output[position], positions[target]);
            return function with { LocalCount = localCount, Instructions = output };

            int Slot(Source source)
            {
                if (source.Read is not { } read) return source.Local;
                if (slots.TryGetValue(read, out int slot)) return slot;
                slots.Add(read, slot = localCount++);
                if (read.Merge is { } merge)
                {
                    foreach (var input in merge.Inputs)
                    {
                        int from = Slot(input.Source);
                        var edge = input.Edge;
                        if (!copies.TryGetValue(edge, out var move)) copies.Add(edge, move = []);
                        move.Add((from, slot));
                    }
                }
                else
                {
                    if (!retained.TryGetValue(read.Position, out var saves)) retained.Add(read.Position, saves = []);
                    saves.Add(slot);
                }
                return slot;
            }

            void EmitBranch(int pc, Instruction instruction)
            {
                int target = instruction.Op == OpCode.IteratorNext ? instruction.B : instruction.A;
                var edge = graph.OutgoingEdges(graph.BlockAt(pc))[0];
                if (!copies.TryGetValue(edge, out var move))
                {
                    branches.Add((output.Count, target));
                    output.Add(instruction);
                    return;
                }
                if (instruction.Op == OpCode.Jump)
                {
                    EmitCopies(move, instruction.Span);
                    branches.Add((output.Count, target));
                    output.Add(instruction);
                    return;
                }
                int fork = output.Count;
                if (instruction.Op == OpCode.IteratorNext)
                {
                    output.Add(instruction with { B = fork + 2 });
                    output.Add(new(OpCode.Jump, Span: instruction.Span));
                    fork++;
                }
                else
                {
                    var inverse = instruction.Op == OpCode.JumpIfTrue ? OpCode.JumpIfFalse : OpCode.JumpIfTrue;
                    output.Add(instruction with { Op = inverse });
                }
                EmitCopies(move, instruction.Span);
                branches.Add((output.Count, target));
                output.Add(new(OpCode.Jump, Span: instruction.Span));
                output[fork] = output[fork] with { A = output.Count };
            }

            void EmitCopies(List<(int From, int To)> moves, SourceSpan? span)
            {
                foreach (var (from, to) in moves)
                {
                    output.Add(new(OpCode.LoadStorage, from, Span: span));
                    output.Add(new(OpCode.InitializeLocal, to, Span: span));
                }
            }
        }

        private static Instruction WithTarget(Instruction instruction, int target) =>
            instruction.Op == OpCode.IteratorNext ? instruction with { B = target } : instruction with { A = target };
    }
}
