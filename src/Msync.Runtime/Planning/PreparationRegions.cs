using Msync.Core;

namespace Msync.Runtime;

internal static class PreparationRegions
{
    internal static bool[] Analyze(IReadOnlyList<Instruction> code, IReadOnlySet<int> headers,
        Func<string?, bool> metadata)
    {
        // Backward targets stop preparation, leaving an acyclic region for this reverse pass.
        // Calls and iterator entry retain opportunities to expose nested or finite I/O plans.
        bool[] opportunities = new bool[code.Count];
        for (int pc = code.Count - 1; pc >= 0; pc--)
        {
            if (headers.Contains(pc)) continue;
            Instruction instruction = code[pc];
            opportunities[pc] = instruction.Op switch
            {
                OpCode.Call or OpCode.Transfer or OpCode.LocalTransfer or OpCode.SelectPath or OpCode.GetIterator => true,
                OpCode.NativeCall when metadata(instruction.Text) => true,
                OpCode.Return or OpCode.IteratorNext => false,
                OpCode.Jump => Reaches(opportunities, instruction.A),
                OpCode.JumpIfTrue or OpCode.JumpIfFalse =>
                    Reaches(opportunities, pc + 1) || Reaches(opportunities, instruction.A),
                _ => Reaches(opportunities, pc + 1)
            };
        }
        return opportunities;
    }

    private static bool Reaches(bool[] opportunities, int pc) =>
        (uint)pc < (uint)opportunities.Length && opportunities[pc];
}
