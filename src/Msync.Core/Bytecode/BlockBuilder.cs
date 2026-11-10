namespace Msync.Core;

public static class BlockBuilder
{
    public static Module Form(Module module) => module with
    { Blocks = module.Functions.SelectMany((function, index) => Partition(function, index)).ToArray() };
    public static IReadOnlyList<BasicBlock> ForFunction(FunctionCode function, int functionIndex) =>
        Partition(function, functionIndex).ToArray();
    private static IEnumerable<BasicBlock> Partition(FunctionCode function, int functionIndex)
    {
        var code = function.Instructions;
        if (code.Count == 0) yield break;
        var starts = new SortedSet<int> { 0, code.Count };
        for (var index = 0; index < code.Count; index++)
        {
            var instruction = code[index];
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) starts.Add(instruction.A);
            if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
            if (IsBoundary(instruction)) { starts.Add(index); starts.Add(index + 1); }
        }
        var boundaries = starts.ToArray();
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            if (start == end) continue;
            var last = code[end - 1];
            IReadOnlyList<int> next = last.Op switch
            {
                OpCode.Return => [],
                OpCode.Jump => [last.A],
                OpCode.JumpIfTrue or OpCode.JumpIfFalse => [last.A, end],
                OpCode.IteratorNext => [last.B, end],
                _ => end < code.Count ? [end] : []
            };
            yield return new(functionIndex, start, end, next, code.Skip(start).Take(end - start).Any(IsEffect));
        }
    }
    private static bool IsBoundary(Instruction i) => IsEffect(i) || i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext or OpCode.Return;
    private static bool IsEffect(Instruction i) => InstructionEffects.Effects(i) != InstructionEffect.None;
}
