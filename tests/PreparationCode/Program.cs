using Msync.Core;
using Msync.Runtime;

int assertions = 0;
int cases = 0;

void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

void Compare(IReadOnlyList<Instruction> code, IReadOnlyDictionary<int, Instruction[]> replacements)
{
    cases++;
    var expected = OriginalRewrite(code, replacements);
    var actual = PreparationCode.Rewrite(code, replacements);
    Check(actual.Targets.SequenceEqual(expected.Targets), $"case {cases}: original-PC targets differ");
    Check(actual.Instructions.SequenceEqual(expected.Instructions), $"case {cases}: instructions, operands or spans differ");
    Check(actual.Sources.SequenceEqual(expected.Sources), $"case {cases}: output-PC provenance differs");
    Check(actual.Targets.Length == code.Count + 1 && actual.Targets[^1] == actual.Instructions.Count,
        $"case {cases}: end-PC target is not the emitted instruction count");
    Check(actual.Sources.Count == actual.Instructions.Count + 1 && actual.Sources[^1] == code.Count,
        $"case {cases}: source sentinel is not the original end PC");
    Check(!actual.Instructions.Any(instruction => instruction.Op == OpCode.Nop), $"case {cases}: Nop survived");
    Check(actual.Targets.Zip(actual.Targets.Skip(1)).All(pair => pair.First <= pair.Second),
        $"case {cases}: targets are not monotone");
    Check(actual.Sources.Zip(actual.Sources.Skip(1)).All(pair => pair.First <= pair.Second),
        $"case {cases}: source positions are not monotone");
}

Compare([], new Dictionary<int, Instruction[]>());
Compare([new(OpCode.Nop), new(OpCode.Nop), new(OpCode.Nop)], new Dictionary<int, Instruction[]>());
var empty = PreparationCode.Rewrite([], new Dictionary<int, Instruction[]>());
Check(empty.Targets.SequenceEqual([0]) && empty.Sources.SequenceEqual([0]), "empty function lost its end-PC sentinels");
var removed = PreparationCode.Rewrite([new(OpCode.Nop), new(OpCode.Null)], new Dictionary<int, Instruction[]> { [1] = [] });
Check(removed.Targets.SequenceEqual([0, 0, 0]) && removed.Sources.SequenceEqual([2]) && removed.Instructions.Count == 0,
    "Nop and empty replacement must collapse onto the same end PC");

Instruction[] branches = [
    new(OpCode.Jump, A: 5, Text: "end", Span: new("branches.msync", 0, 4)),
    new(OpCode.JumpIfTrue, A: 0, Span: new("branches.msync", 5, 4)),
    new(OpCode.JumpIfFalse, A: 3, Span: new("branches.msync", 10, 4)),
    new(OpCode.IteratorNext, A: 7, B: 5, Text: "iterator", Span: new("branches.msync", 15, 4)),
    new(OpCode.Nop, Span: new("branches.msync", 20, 4))
];
Dictionary<int, Instruction[]> branchReplacements = new()
{
    [1] = [new(OpCode.Nop), new(OpCode.True), new(OpCode.JumpIfTrue, A: 5)],
    [3] = []
};
Compare(branches, branchReplacements);
var collapsed = PreparationCode.Rewrite(branches, branchReplacements);
Check(collapsed.Targets.SequenceEqual([0, 1, 3, 4, 4, 4]), "collapsed branch target fixture changed");
Check(collapsed.Sources.SequenceEqual([0, 1, 1, 2, 5]), "expanded instructions lost their shared original PC");
Check(collapsed.Instructions.Select(instruction => instruction.A).SequenceEqual([4, 0, 4, 4]),
    "end and removed-instruction branches were not redirected to the same PC");
Compare(branches, new Dictionary<int, Instruction[]> { [4] = [new(OpCode.False), new(OpCode.Null)] });
var iterator = PreparationCode.Rewrite(branches, new Dictionary<int, Instruction[]> { [4] = [new(OpCode.False), new(OpCode.Null)] });
Check(iterator.Instructions[3] is { Op: OpCode.IteratorNext, A: 7, B: 6, Text: "iterator" },
    "IteratorNext must remap B while retaining its local-slot A and text");

Instruction ordinary = new(OpCode.GetMember, A: 2, B: 19, Text: ".codex\0成员", Span: new("source-猫.msync", 17, 9));
Compare([ordinary], new Dictionary<int, Instruction[]>());
Check(ReferenceEquals(PreparationCode.Rewrite([ordinary], new Dictionary<int, Instruction[]>()).Instructions[0], ordinary),
    "an unchanged non-control instruction was copied or altered");

Random random = new(0x5A17);
for (int sample = 0; sample < 1500; sample++)
{
    int length = sample % 7 == 0 ? random.Next(192, 513) : random.Next(0, 97);
    Instruction[] code = Enumerable.Range(0, length).Select(index => Generate(random, length, index)).ToArray();
    Dictionary<int, Instruction[]> replacements = [];
    for (int index = 0; index < length; index++)
    {
        if (random.Next(5) != 0) continue;
        replacements.Add(index, Enumerable.Range(0, random.Next(5))
            .Select(offset => Generate(random, length, index, offset)).ToArray());
    }
    Compare(code, replacements);
}

Console.WriteLine($"Preparation code emission: {cases} cases, {assertions} exact/provenance contracts passed.");

static Instruction Generate(Random random, int length, int index, int offset = 0)
{
    SourceSpan? span = random.Next(4) == 0 ? null : new("generated-λ.msync", index * 16 + offset, random.Next(1, 5));
    int target = random.Next(length + 1);
    return random.Next(12) switch
    {
        0 => new(OpCode.Nop, Span: span),
        1 => new(OpCode.Jump, A: target, Span: span),
        2 => new(OpCode.JumpIfTrue, A: target, Span: span),
        3 => new(OpCode.JumpIfFalse, A: target, Span: span),
        4 => new(OpCode.IteratorNext, A: random.Next(8), B: target, Text: "iterator", Span: span),
        5 => new(OpCode.GetMember, A: random.Next(8), B: index, Text: ".codex", Span: span),
        6 => new(OpCode.MakeScope, A: 2, B: index, Text: "first\0成员", Span: span),
        7 => new(OpCode.Constant, A: random.Next(8), Span: span),
        8 => new(OpCode.LoadLocal, A: random.Next(8), Span: span),
        9 => new(OpCode.True, Span: span),
        10 => new(OpCode.Return, Span: span),
        _ => new(OpCode.Null, Span: span)
    };
}

// The pre-extraction emitter from 4199e9d, retained as an independent allocation-heavy oracle.
static (int[] Targets, List<Instruction> Instructions, List<int> Sources) OriginalRewrite(
    IReadOnlyList<Instruction> code, IReadOnlyDictionary<int, Instruction[]> replacements)
{
    int[] positions = new int[code.Count + 1];
    List<Instruction> output = [];
    List<int> sources = [];
    for (int index = 0; index < code.Count; index++)
    {
        positions[index] = output.Count;
        Instruction[] rewritten = (replacements.GetValueOrDefault(index) ?? [code[index]])
            .Where(instruction => instruction.Op != OpCode.Nop).ToArray();
        output.AddRange(rewritten);
        sources.AddRange(Enumerable.Repeat(index, rewritten.Length));
    }
    positions[^1] = output.Count;
    sources.Add(code.Count);
    for (int index = 0; index < output.Count; index++)
    {
        Instruction instruction = output[index];
        output[index] = instruction.Op switch
        {
            OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => instruction with { A = positions[instruction.A] },
            OpCode.IteratorNext => instruction with { B = positions[instruction.B] },
            _ => instruction
        };
    }
    return (positions, output, sources);
}
