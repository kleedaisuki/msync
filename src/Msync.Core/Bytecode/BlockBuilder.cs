namespace Msync.Core;

public static class BlockBuilder
{
    public static Module Form(Module module) => module with
    { Blocks = module.Functions.SelectMany((function, index) => Partition(function, index)).ToArray() };
    public static IReadOnlyList<BasicBlock> ForFunction(FunctionCode function, int functionIndex) =>
        Partition(function, functionIndex).ToArray();

    public static BasicBlock[] IndexFunction(FunctionCode function, int functionIndex)
    {
        var index = new BasicBlock[function.Instructions.Count];
        foreach (var block in Partition(function, functionIndex))
            Array.Fill(index, block, block.Start, block.End - block.Start);
        return index;
    }

    private static IEnumerable<BasicBlock> Partition(FunctionCode function, int functionIndex)
    {
        var code = function.Instructions;
        if (code.Count == 0) yield break;
        const byte boundary = 1, effect = 2;
        var marks = new byte[code.Count + 1];
        marks[0] = marks[^1] = boundary;
        for (var index = 0; index < code.Count; index++)
        {
            var instruction = code[index];
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) marks[instruction.A] |= boundary;
            if (instruction.Op == OpCode.IteratorNext) marks[instruction.B] |= boundary;
            bool hasEffects = InstructionEffects.Effects(instruction) != InstructionEffect.None;
            if (hasEffects) marks[index] |= effect;
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext or OpCode.Return)
                marks[index + 1] |= boundary;
        }
        var start = 0;
        bool effects = (marks[0] & effect) != 0;
        for (var end = 1; end <= code.Count; end++)
        {
            if ((marks[end] & boundary) == 0)
            {
                effects |= (marks[end] & effect) != 0;
                continue;
            }
            var last = code[end - 1];
            IReadOnlyList<int> next = last.Op switch
            {
                OpCode.Return => [],
                OpCode.Jump => [last.A],
                OpCode.JumpIfTrue or OpCode.JumpIfFalse => [last.A, end],
                OpCode.IteratorNext => [last.B, end],
                _ => end < code.Count ? [end] : []
            };
            yield return new(functionIndex, start, end, next, effects);
            start = end;
            effects = (marks[end] & effect) != 0;
        }
    }
}
