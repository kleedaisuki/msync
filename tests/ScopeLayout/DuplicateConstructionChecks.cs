using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class DuplicateConstructionChecks
{
    internal static async Task Run()
    {
        int checks = 0;
        int cases = 0;
        string root = Path.GetFullPath(".temp/duplicate-construction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string scenario in new[] { "pure-entry", "prior-transfer", "called" })
                foreach (bool selected in new[] { false, true })
                    foreach (string route in new[] { "module", "object", "executable" })
                        await RunCase(scenario, selected, route);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Duplicate construction: {cases} selected-effect journeys, {checks} diagnostic/order contracts passed.");

        async Task RunCase(string scenario, bool selected, string route)
        {
            cases++;
            string name = $"{scenario}-{selected}-{route}";
            string work = Path.Combine(root, name);
            Directory.CreateDirectory(work);
            SourceSpan scopeSpan = new(name + ".msync", 41, 23);
            Module program = Build(scenario, selected, scopeSpan);
            if (route == "object") program = RoundTrip(program);
            if (route == "executable")
            {
                Module bound = Binding.Bind(program, new(work, work));
                program = RoundTrip(ContextualBackend.Prepare(bound, new(WorkingDirectory: work)));
            }
            Check(!File.Exists(Path.Combine(work, "before.txt")) && !File.Exists(Path.Combine(work, "after.txt")),
                name + ": preparation executed an output transfer");
            MsyncException? failure = null;
            try
            {
                Value result = await Interpreter.ExecuteAsync(program, new(WorkingDirectory: work));
                Check(result is NullValue, name + ": unselected constructor changed the program result");
            }
            catch (MsyncException error) { failure = error; }
            Check((failure is not null) == selected, name + ": duplicate constructor did not follow the selected control-flow edge");
            if (failure is not null)
            {
                Check(failure.Span == scopeSpan, name + ": concrete constructor error lost its owning source span");
                Check(failure.InnerException is ArgumentException argument && argument.Message.Contains("same key", StringComparison.Ordinal),
                    name + ": constructor failure escaped without the concrete duplicate-field cause");
            }
            bool hasTransfers = scenario != "pure-entry";
            if (hasTransfers)
            {
                Check(File.ReadAllText(Path.Combine(work, "before.txt")) == "before",
                    name + ": selected failure discarded or never admitted its preceding transfer");
                if (selected)
                    Check(!File.Exists(Path.Combine(work, "after.txt")), name + ": an operation after the selected failure executed");
                else
                    Check(File.ReadAllText(Path.Combine(work, "after.txt")) == "after", name + ": unselected failure suppressed later work");
            }
            else
                Check(!File.Exists(Path.Combine(work, "before.txt")) && !File.Exists(Path.Combine(work, "after.txt")),
                    name + ": pure entry acquired output effects");
        }

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static Module Build(string scenario, bool selected, SourceSpan scopeSpan)
    {
        Instruction condition = new(selected ? OpCode.True : OpCode.False);
        Instruction duplicate = new(OpCode.MakeScope, A: 2, Text: "item\0item", Span: scopeSpan);
        Instruction[] pure = [condition, new(OpCode.JumpIfFalse, 7), new(OpCode.True), new(OpCode.False),
            duplicate, new(OpCode.Pop), new(OpCode.Jump, 7), new(OpCode.Null), new(OpCode.Return)];
        if (scenario == "pure-entry")
            return new([], [new("main", 0, 0, pure, TypeRef.Null)], OptimizationLevel: 0);

        Value[] constants = [new StringValue("before"), new PathValue("./before.txt"),
            new StringValue("after"), new PathValue("./after.txt")];
        List<Instruction> code = [new(OpCode.Constant, 0), new(OpCode.Constant, 1),
            new(OpCode.Transfer), new(OpCode.Pop)];
        if (scenario == "called")
        {
            code.AddRange([new(OpCode.MakeClosure, 1), condition, new(OpCode.Call, A: 1, B: 2), new(OpCode.Pop)]);
        }
        else
        {
            code.AddRange([condition, new(OpCode.JumpIfFalse, 11), new(OpCode.True), new(OpCode.False),
                duplicate, new(OpCode.Pop), new(OpCode.Jump, 11)]);
        }
        code.AddRange([new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Transfer), new(OpCode.Pop),
            new(OpCode.Null), new(OpCode.Return)]);
        List<FunctionCode> functions = [new("main", 0, 0, code, TypeRef.Null)];
        if (scenario == "called")
            functions.Add(new("construct", 1, 1,
                [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 7), new(OpCode.True), new(OpCode.False),
                 duplicate, new(OpCode.Pop), new(OpCode.Jump, 7), new(OpCode.Null), new(OpCode.Return)],
                TypeRef.Null, ParameterTypes: [TypeRef.Bool]));
        return new(constants, functions, OptimizationLevel: 0);
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
