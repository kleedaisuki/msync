using Msync.Core;
using Msync.Runtime;

internal static class RegionsChecks
{
    internal static void Run()
    {
        int cases = 0;
        int assertions = 0;

        void Compare(IReadOnlyList<Instruction> code, bool[]? expected = null)
        {
            cases++;
            HashSet<int> headers = [];
            for (int pc = 0; pc < code.Count; pc++)
                if (code[pc].Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse && code[pc].A <= pc)
                    headers.Add(code[pc].A);
            bool[] actual = PreparationRegions.Analyze(code, headers, IsMetadata);
            Require(actual.Length == code.Count, $"case {cases}: region map length changed");
            for (int pc = 0; pc < code.Count; pc++)
                Require(actual[pc] == CanReachOpportunity(code, headers, pc), $"case {cases}, PC {pc}: DFS reachability differs");
            if (expected is not null)
                Require(actual.SequenceEqual(expected), $"case {cases}: explicit selected-frontier fixture changed");
        }

        void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        Compare([], []);
        Compare([new(OpCode.Return), new(OpCode.Transfer)], [false, true]);
        Compare([new(OpCode.NativeCall, Text: "storage.buffer_push"), new(OpCode.Return)], [false, false]);
        Compare([new(OpCode.NativeCall, Text: "fs.exists"), new(OpCode.Return)], [true, false]);
        Compare([new(OpCode.NativeCall), new(OpCode.Null), new(OpCode.Return)], [false, false, false]);
        Compare([new(OpCode.Jump, A: 3), new(OpCode.Call), new(OpCode.Null), new(OpCode.Return)], [false, true, false, false]);
        Compare([new(OpCode.JumpIfTrue, A: 3), new(OpCode.Call), new(OpCode.Return), new(OpCode.Null)], [true, true, false, false]);
        Compare([new(OpCode.JumpIfFalse, A: 3), new(OpCode.Return), new(OpCode.Null), new(OpCode.SelectPath)], [true, false, true, true]);
        Compare([new(OpCode.Call), new(OpCode.Jump, A: 0)], [false, false]);
        Compare([new(OpCode.GetIterator), new(OpCode.Call), new(OpCode.Jump, A: 1)], [true, false, false]);
        Compare([new(OpCode.IteratorNext, A: 2, B: 2), new(OpCode.Transfer)], [false, true]);
        Compare([new(OpCode.Jump, A: 1)], [false]);
        Compare([new(OpCode.Null), new(OpCode.LocalTransfer)], [true, true]);

        Random random = new(0x71A17);
        for (int sample = 0; sample < 1200; sample++)
        {
            int length = sample % 11 == 0 ? random.Next(192, 513) : random.Next(0, 97);
            Instruction[] code = Enumerable.Range(0, length).Select(_ => Generate(random, length)).ToArray();
            Compare(code);
        }
        Console.WriteLine($"Preparation regions: {cases} cases, {assertions} DFS/frontier contracts passed.");
    }

    private static bool IsMetadata(string? name) => name is "fs.exists" or "fs.status";

    private static bool CanReachOpportunity(IReadOnlyList<Instruction> code, IReadOnlySet<int> headers, int start)
    {
        Stack<int> pending = new();
        HashSet<int> visited = [];
        pending.Push(start);
        while (pending.TryPop(out int pc))
        {
            if (pc == code.Count || headers.Contains(pc) || !visited.Add(pc)) continue;
            Instruction instruction = code[pc];
            if (instruction.Op is OpCode.Call or OpCode.Transfer or OpCode.LocalTransfer or OpCode.SelectPath or OpCode.GetIterator
                || instruction.Op == OpCode.NativeCall && IsMetadata(instruction.Text)) return true;
            if (instruction.Op is OpCode.Return or OpCode.IteratorNext) continue;
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) pending.Push(instruction.A);
            if (instruction.Op != OpCode.Jump) pending.Push(pc + 1);
        }
        return false;
    }

    private static Instruction Generate(Random random, int length)
    {
        int target = random.Next(length + 1);
        return random.Next(15) switch
        {
            0 => new(OpCode.Jump, A: target),
            1 => new(OpCode.JumpIfTrue, A: target),
            2 => new(OpCode.JumpIfFalse, A: target),
            3 => new(OpCode.IteratorNext, A: random.Next(8), B: target),
            4 => new(OpCode.Call),
            5 => new(OpCode.Transfer),
            6 => new(OpCode.LocalTransfer),
            7 => new(OpCode.SelectPath),
            8 => new(OpCode.GetIterator),
            9 => new(OpCode.NativeCall, Text: "fs.exists"),
            10 => new(OpCode.NativeCall, Text: "fs.status"),
            11 => new(OpCode.NativeCall, Text: "storage.buffer_push"),
            12 => new(OpCode.NativeCall, Text: "math.abs"),
            13 => new(OpCode.Return),
            _ => new(OpCode.Null)
        };
    }
}
