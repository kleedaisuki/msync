using Msync.Core;

namespace Msync.Runtime;

internal static class PreparationCode
{
    internal static (int[] Targets, List<Instruction> Instructions, List<int> Sources) Rewrite(
        IReadOnlyList<Instruction> code, IReadOnlyDictionary<int, Instruction[]> replacements)
    {
        int[] targets = new int[code.Count + 1];
        List<Instruction> output = new(code.Count);
        List<int> sources = new(code.Count + 1);
        for (int index = 0; index < code.Count; index++)
        {
            targets[index] = output.Count;
            if (replacements.TryGetValue(index, out var rewritten))
            {
                foreach (Instruction instruction in rewritten) Append(instruction, index, output, sources);
            }
            else Append(code[index], index, output, sources);
        }
        targets[^1] = output.Count;
        sources.Add(code.Count);
        for (int index = 0; index < output.Count; index++)
        {
            Instruction instruction = output[index];
            output[index] = instruction.Op switch
            {
                OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => instruction with { A = targets[instruction.A] },
                OpCode.IteratorNext => instruction with { B = targets[instruction.B] },
                _ => instruction
            };
        }
        return (targets, output, sources);
    }

    private static void Append(Instruction instruction, int source, List<Instruction> output, List<int> sources)
    {
        if (instruction.Op == OpCode.Nop) return;
        output.Add(instruction);
        sources.Add(source);
    }
}
