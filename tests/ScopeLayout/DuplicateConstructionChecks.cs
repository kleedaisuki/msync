using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class DuplicateConstructionChecks
{
    private const int Null = 0, Bool = 1, Any = 2, Scope = 3;
    private static readonly CodeType[] Types = [new(TypeKind.Null), new(TypeKind.Bool), new(TypeKind.Any), new(TypeKind.Scope)];

    internal static async Task Run()
    {
        int checks = 0, journeys = 0;
        string root = Path.GetFullPath(".temp/scope-selected-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool selected in new[] { false, true })
            {
                RejectDuplicate(selected);
                foreach (string scenario in new[] { "pure-entry", "prior-transfer", "called" })
                    foreach (string route in new[] { "module", "object", "executable" })
                        await RunCase(scenario, selected, route);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS duplicate layout intake and {journeys} selected-effect journeys, {checks} contracts");

        void RejectDuplicate(bool selected)
        {
            var module = Build("pure-entry", selected, new("duplicate-layout.msync", 41, 23)) with
            {
                ScopeLayouts = [new([new("other", Bool), new("other", Bool)])]
            };
            using MemoryStream malformed = new();
            Artifact.WriteProduced(module, malformed);
            malformed.Position = 0;
            try { _ = Artifact.Read(malformed); }
            catch (MsyncException error) when (error.Message.Contains("duplicate field name", StringComparison.Ordinal))
            {
                checks++;
                return;
            }
            throw new InvalidOperationException($"The reader admitted duplicate field names (selected={selected}).");
        }

        async Task RunCase(string scenario, bool selected, string route)
        {
            journeys++;
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

            string before = Path.Combine(work, "before.txt"), after = Path.Combine(work, "after.txt");
            Check(!File.Exists(before) && !File.Exists(after), name + ": preparation executed an output transfer");
            MsyncException? failure = null;
            try
            {
                Value result = await Interpreter.ExecuteAsync(program, new(WorkingDirectory: work));
                Check(result is NullValue, name + ": unselected field failure changed the program result");
            }
            catch (MsyncException error) { failure = error; }
            Check((failure is not null) == selected, name + ": opaque field failure did not follow the selected edge");
            if (failure is not null)
            {
                Check(failure.Span == scopeSpan, name + ": selected error lost its field source span");
                Check(failure.Message.Contains("Scope has no member 'item'", StringComparison.Ordinal),
                    name + ": dynamic lookup lost its concrete diagnostic");
            }
            if (scenario == "pure-entry")
                Check(!File.Exists(before) && !File.Exists(after), name + ": pure entry acquired output effects");
            else
            {
                Check(File.ReadAllText(before) == "before", name + ": earlier selected transfer was lost");
                if (selected) Check(!File.Exists(after), name + ": an operation after the failure executed");
                else Check(File.ReadAllText(after) == "after", name + ": an unselected failure suppressed later work");
            }
        }

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static Module Build(string scenario, bool selected, SourceSpan fieldSpan)
    {
        Instruction condition = new(selected ? OpCode.True : OpCode.False);
        FunctionCode read = new("read opaque member", 1, 1,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Span: fieldSpan), new(OpCode.Return)], TypeRef.Any,
            ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([Scope], Any, []) };
        var code = new List<Instruction>();
        if (scenario != "pure-entry")
            code.AddRange([new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Pop)]);
        if (scenario == "called")
            code.AddRange([new(OpCode.MakeClosure, 2), condition, new(OpCode.Call, 1, 3), new(OpCode.Pop)]);
        else
            AppendChoice(code, condition);
        if (scenario != "pure-entry")
            code.AddRange([new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Transfer), new(OpCode.Pop)]);
        code.AddRange([new(OpCode.Null), new(OpCode.Return)]);

        List<FunctionCode> functions =
        [
            new FunctionCode("main", 0, 0, code, TypeRef.Null) { CodeSignature = new([], Null, []) }, read
        ];
        if (scenario == "called")
        {
            List<Instruction> body = [];
            AppendChoice(body, new(OpCode.LoadLocal, 0));
            body.AddRange([new(OpCode.Null), new(OpCode.Return)]);
            functions.Add(new("selected construction and read", 1, 1, body, TypeRef.Null, ParameterTypes: [TypeRef.Bool])
            { CodeSignature = new([Bool], Null, []) });
        }
        Value[] constants = scenario == "pure-entry" ? [] : [new StringValue("before"), new PathValue("./before.txt"),
            new StringValue("after"), new PathValue("./after.txt")];
        return new(constants, functions, OptimizationLevel: 0)
        {
            CodeTypes = Types, ScopeLayouts = [new([new("other", Bool)])], FieldProjections = [new("item", [])]
        };
    }

    private static void AppendChoice(List<Instruction> code, Instruction condition)
    {
        code.Add(condition);
        int jump = code.Count;
        code.AddRange([new(OpCode.JumpIfFalse), new(OpCode.MakeClosure, 1), new(OpCode.True),
            new(OpCode.NewScope), new(OpCode.Call, 1, 2), new(OpCode.Pop)]);
        code[jump] = code[jump] with { A = code.Count };
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
