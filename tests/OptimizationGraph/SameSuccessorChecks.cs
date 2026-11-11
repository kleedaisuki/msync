using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class SameSuccessorChecks
{
    private static readonly SourceSpan BranchSpan = new("same-successor.msync", 18, 2);
    private static readonly SourceSpan MemberSpan = new("same-successor.msync", 4, 7);

    internal static async Task<int> Run(string directory)
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        foreach (var branch in new[] { OpCode.JumpIfFalse, OpCode.JumpIfTrue })
        {
            var ready = Program([], ReadLine(), new(OpCode.IsNull), new(branch, 3), ReadLine(), new(OpCode.Return));
            foreach (var level in new[] { 1, 2, 3 })
                foreach (var candidate in Artifacts(ready, level, directory))
                {
                    var code = candidate.Functions[0].Instructions;
                    Check(!code.Any(IsBranch) && code.Count(instruction => instruction.Text == "io.read_line") == 2,
                        $"O{level}/{candidate.Stage}: ready branch or observable input demand was not simplified correctly.");
                    Check(await Execute(candidate, directory, "discarded\nsecond\n") is StringValue { Data: "second" },
                        "Eliminating the branch also eliminated the first input read.");
                }
        }

        var merged = Program([], ReadLine(), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 6),
            ReadLine(), new(OpCode.IsNull), new(OpCode.Jump, 8),
            ReadLine(), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 9), ReadLine(), new(OpCode.Return));
        foreach (var candidate in Artifacts(merged, 2, directory))
        {
            Check(candidate.Functions[0].Instructions.Count(IsBranch) == 1,
                "A ready Bool joining from two predecessors kept a redundant branch.");
            Check(await Execute(candidate, directory, "present\ndiscarded\nthird\n") is StringValue { Data: "third" },
                "Dropping a branch with joined operands changed input consumption.");
            Check(await Execute(candidate, directory, "") is NullValue, "The selected empty-input path changed.");
        }

        var failure = Program([], new(OpCode.MakeScope), new(OpCode.GetMember, Text: "absent", Span: MemberSpan),
            new(OpCode.IsNull), new(OpCode.JumpIfTrue, 4, Span: BranchSpan), new(OpCode.True), new(OpCode.Return));
        foreach (var candidate in Artifacts(failure, 2, directory))
        {
            Check(!candidate.Functions[0].Instructions.Any(IsBranch), "The branch after the failing producer was retained.");
            var error = await Failure(Execute(candidate, directory));
            Check(error.Span == MemberSpan && error.Message.Contains("absent", StringComparison.Ordinal),
                "Removing the branch lost the selected member error.");
        }

        var unknown = Program([new ScopeValue(new Dictionary<string, Value> { ["condition"] = new StringValue("not Bool") })],
            new(OpCode.Constant), new(OpCode.GetMember, Text: "condition"), new(OpCode.JumpIfTrue, 3, Span: BranchSpan),
            new(OpCode.True), new(OpCode.Return));
        foreach (var candidate in Artifacts(unknown, 2, directory))
        {
            Check(candidate.Functions[0].Instructions.Any(IsBranch), "An unproved condition was treated as an immediate Bool.");
            var error = await Failure(Execute(candidate, directory));
            Check(error.Span == BranchSpan && error.Message.Contains("Condition requires Bool", StringComparison.Ordinal),
                "Removing a branch erased its condition-type error.");
        }

        foreach (var level in new[] { 0, 2 })
        {
            TaskCompletionSource<ProcessValue> producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var deferred = Program([new ProcessValue(0, "", "") { Completion = producer.Task }],
                new(OpCode.Constant), new(OpCode.GetMember, Text: "success"), new(OpCode.JumpIfTrue, 3, Span: BranchSpan),
                new(OpCode.True), new(OpCode.Return));
            var pending = Execute(Optimizer.OptimizeInstructions(deferred, level), directory);
            bool awaited = !pending.IsCompleted;
            producer.SetException(new MsyncException("selected deferred failure"));
            Check(awaited, "Dropping a branch let execution pass a deferred Bool.");
            var error = await Failure(pending);
            Check(error.Span == BranchSpan && error.Message == "selected deferred failure", "The selected deferred failure was lost.");
        }

        var transfer = Program([new PathValue("./same-successor-output")],
            ReadLine(), new(OpCode.Constant, 0), new(OpCode.Transfer), ReadLine(),
            new(OpCode.IsNull), new(OpCode.JumpIfFalse, 6), new(OpCode.Return));
        foreach (var candidate in Artifacts(transfer, 2, directory))
        {
            var code = candidate.Functions[0].Instructions;
            int selected = code.ToList().FindIndex(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer);
            Check(selected >= 0 && code[selected + 1].Op != OpCode.Pop, "An awaited publication became a discarded transfer.");
            Check(!code.Any(IsBranch), "An unrelated transfer prevented a safe same-successor cleanup.");
            string destination = Path.Combine(directory, "same-successor-output");
            File.Delete(destination);
            Check(await Execute(candidate, directory, "payload\ndiscarded\n") is BoolValue { Data: true }
                && await File.ReadAllTextAsync(destination) == "payload",
                "The selected transfer result or published bytes changed.");
        }

        var joinedTransfer = Program([new PathValue("./same-successor-join-output"), new StringValue("payload")],
            ReadLine(), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 7),
            new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.Transfer), new(OpCode.Jump, 8),
            new(OpCode.Null), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 10), new(OpCode.True), new(OpCode.Return));
        foreach (var candidate in Artifacts(joinedTransfer, 2, directory))
        {
            var code = candidate.Functions[0].Instructions;
            int selected = code.ToList().FindIndex(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer);
            Check(selected >= 0 && code[selected + 1].Op != OpCode.Pop && code.Count(IsBranch) == 2,
                "Dropping a join's last demand changed the transfer's execution boundary.");
            string destination = Path.Combine(directory, "same-successor-join-output");
            File.Delete(destination);
            Check(await Execute(candidate, directory, "") is BoolValue { Data: true } && !File.Exists(destination),
                "The null alternative published an unselected transfer.");
            Check(await Execute(candidate, directory, "selected\n") is BoolValue { Data: true }
                && await File.ReadAllTextAsync(destination) == "payload",
                "The selected transfer through a join changed its publication.");
        }
        return assertions;
    }

    private static Instruction ReadLine() => new(OpCode.NativeCall, Text: "io.read_line");
    private static bool IsBranch(Instruction instruction) => instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse;
    private static Module Program(Value[] constants, params Instruction[] code) =>
        new(constants, [new("main", 0, 0, code, TypeRef.Any)], OptimizationLevel: 0);

    private static IEnumerable<Module> Artifacts(Module source, int level, string directory)
    {
        var portable = Optimizer.OptimizeInstructions(source, level);
        foreach (var module in new[] { portable, Binding.Bind(portable, new(directory, directory)) })
        {
            using MemoryStream bytes = new();
            Artifact.Write(module, bytes);
            bytes.Position = 0;
            yield return Artifact.Read(bytes);
        }
    }

    private static async Task<Value> Execute(Module module, string directory, string input = "")
    {
        using StringReader reader = new(input);
        return await Interpreter.ExecuteAsync(module, new ExecutionOptions(WorkingDirectory: directory, Output: TextWriter.Null, Error: TextWriter.Null)
        { Input = reader });
    }

    private static async Task<MsyncException> Failure(Task<Value> pending)
    {
        try { await pending; }
        catch (MsyncException error) { return error; }
        throw new InvalidOperationException("The selected bytecode error did not execute.");
    }
}
