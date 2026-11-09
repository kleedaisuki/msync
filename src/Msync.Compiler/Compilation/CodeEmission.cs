using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private static void AppendCode(Frame frame, IReadOnlyList<Instruction> code)
    {
        int offset = frame.Code.Count;
        foreach (var instruction in code)
            frame.Code.Add(instruction.Op switch
            {
                OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => instruction with { A = instruction.A + offset },
                OpCode.IteratorNext => instruction with { B = instruction.B + offset },
                _ => instruction
            });
    }
}
