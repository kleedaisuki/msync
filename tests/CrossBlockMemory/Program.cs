using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
MethodInfo transform = typeof(Optimizer).Assembly.GetType("Msync.Core.CrossBlockMemoryOptimization")!
    .GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
Module Rewrite(Module original)
{
    var literals = original.Constants.ToList();
    return original with
    {
        Functions = original.Functions.Select(function => (FunctionCode)transform.Invoke(null, [original, function, literals])!).ToArray(),
        Constants = literals,
        Blocks = null
    };
}
string Snapshot(Value value) => value switch
{
    ScopeValue scope => "{" + string.Join(',', scope.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key + ":" + Snapshot(pair.Value))) + "}",
    NumberValue number => "Number:" + BitConverter.DoubleToInt64Bits(number.Data),
    _ => value.Type + ":" + value
};
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new(); Artifact.Write(module, bytes); bytes.Position = 0; return Artifact.Read(bytes);
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "cross-block-memory-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    async Task<string> Execute(Module module)
    {
        try { return Snapshot(await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root))); }
        catch (Exception error) when (error is MsyncException or ArgumentException)
        { return "error:" + error.Message; }
    }
    var examples = new (string Name, string Source, bool Eliminate)[]
    {
        ("branch-phi", "let box = scope { field: \"initial\" }; if exists ./flag { box::field = \"left\"; } else { box::field = \"right\"; }; box::field", true),
        ("alias-phi", "let box = scope { field: \"initial\" }; let alias = box; if exists ./flag { alias = box; alias::field = \"left\"; } else { box::field = \"right\"; }; alias::field", true),
        ("disjoint-fields", "let box = scope { left: 0, right: 0 }; if exists ./flag { box::left = 11; } else { box::right = 29; }; box::left + box::right", true),
        ("loop-carried-field", "let box = scope { sum: 0 }; for item in [1, 2, 3, 4] { box::sum = box::sum + item; }; box::sum", true),
        ("loop-with-branch", "let box = scope { sum: 0 }; for item in [1, 2, 3, 4] { if exists ./flag { box::sum = box::sum + item; } else { box::sum = box::sum + 1; }; }; box::sum", true),
        ("escaped-return", "let box = scope { field: 0 }; let alias = box; if exists ./flag { alias::field = 3; }; box", false),
        ("per-iteration-instances", "let previous = scope { item: 0 }; for item in [1, 2] { let next = scope { item: item }; previous = next; }; previous::item", false),
        ("captured-field", "let box = scope { item: 0, read: fn { item } }; if exists ./flag { box::item = 3; }; box::read()", false),
        ("captured-binding", "let box = scope { item: 0 }; let update = fn { box::item = 7; }; if exists ./flag { update(); }; box::item", false),
        ("return-in-branch", "let box = scope { item: 0 }; if exists ./flag { box::item = 7; box } else { box::item = 9; box }", false),
        ("two-instances", "let box = scope { item: 0 }; let other = scope { item: 2 }; let alias = box; if exists ./flag { alias = other; }; alias::item = 9; box::item + other::item", false),
        ("private-across-fs-read", "let box = scope { item: 0 }; if exists ./flag { box::item = 3; }; exists ./unrelated; box::item", true),
        ("private-across-plain-barriers", "let box = scope { item: 0 }; if exists ./flag { box::item = 3; }; release; acquire; box::item", true),
        ("scope-null-test", "let box = scope { item: 0 }; if exists ./flag { box::item = 3; }; box is null", false),
        ("nested-scope", "let inner = scope { item: 0 }; let outer = scope { inner: inner }; if exists ./flag { inner::item = 3; }; outer::inner::item", false)
    };
    foreach (var example in examples)
    {
        Module original = Compiler.Compile(example.Source, example.Name, 0);
        Module rewritten = Rewrite(original);
        if (example.Eliminate)
        {
            Check(rewritten.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.MakeScope or OpCode.GetMember or OpCode.SetMember)),
                example.Name + ": cross-block object/field operation survived");
        }
        Module again = Rewrite(rewritten);
        Check(again.Functions.SelectMany(function => function.Instructions).SequenceEqual(rewritten.Functions.SelectMany(function => function.Instructions)),
            example.Name + ": transform is not idempotent");
        foreach (bool present in new[] { false, true })
        {
            string flag = Path.Combine(root, "flag");
            if (present) await File.WriteAllTextAsync(flag, "present"); else File.Delete(flag);
            string expected = await Execute(original);
            Check(await Execute(rewritten) == expected, example.Name + ": direct result differs");
            foreach (int level in new[] { 0, 2, 3 })
            {
                Module optimized = Optimizer.Optimize(rewritten, level);
                Check(await Execute(optimized) == expected, example.Name + $": O{level} result differs");
                Check(await Execute(RoundTrip(optimized)) == expected, example.Name + $": O{level} mso differs");
                Check(await Execute(RoundTrip(Binding.Bind(optimized, new(root, root)))) == expected,
                    example.Name + $": O{level} mexe differs");
            }
        }
    }

    Module scalarPhi = new([new NumberValue(3)], [new("scalar-phi", 1, 2,
        [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 6), new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "item"),
         new(OpCode.StoreLocal, 1), new(OpCode.Jump, 8), new(OpCode.True), new(OpCode.StoreLocal, 1),
         new(OpCode.LoadLocal, 1), new(OpCode.GetMember, Text: "item"), new(OpCode.Return)], TypeRef.Any, ParameterTypes: [TypeRef.Bool])], OptimizationLevel: 0);
    Module scalarPhiTransformed = Rewrite(scalarPhi);
    Check(scalarPhiTransformed.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.MakeScope),
        "A scalar/scope phi removed the potentially failing physical receiver");
    foreach (bool value in new[] { false, true })
    {
        Module wrapped = scalarPhi with
        {
            Functions = [new("entry", 0, 0,
                [new(OpCode.MakeClosure, 1), new(value ? OpCode.True : OpCode.False), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Any),
                scalarPhi.Functions[0]]
        };
        Check(await Execute(Rewrite(wrapped)) == await Execute(wrapped), "A scalar/scope merge changed a selected result/error");
    }

    Module duplicate = new([], [new("duplicate", 0, 1,
        [new(OpCode.True), new(OpCode.False), new(OpCode.MakeScope, 2, Text: "item\0item"), new(OpCode.StoreLocal, 0),
         new(OpCode.True), new(OpCode.JumpIfFalse, 8), new(OpCode.LoadLocal, 0), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)], OptimizationLevel: 0);
    Check(await Execute(Rewrite(duplicate)) == await Execute(duplicate), "Duplicate-member construction error changed");
    Module missing = new([], [new("missing", 0, 1,
        [new(OpCode.True), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.StoreLocal, 0),
         new(OpCode.True), new(OpCode.JumpIfFalse, 7), new(OpCode.LoadLocal, 0), new(OpCode.Pop),
         new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "missing"), new(OpCode.Return)], TypeRef.Any)], OptimizationLevel: 0);
    Check(await Execute(Rewrite(missing)) == await Execute(missing), "Missing-field selected error changed");
    Module persistent = Compiler.Compile("let box = scope { item: 1 }; if exists ./flag { box::item = 7; }; box::item", optimization: 0) with { PreserveLocals = true };
    Check(ReferenceEquals(Rewrite(persistent).Functions[0], persistent.Functions[0]), "Persistent REPL storage was scalarized");

    const string copy = "let box = scope { payload: \"before\", target: ./result }; if exists ./flag { box::payload = \"你好 Klee\"; } else { box::payload = \"other branch\"; }; for item in [1, 2] { box::target = ./result; }; box::payload -> box::target;";
    Module copyOriginal = Compiler.Compile(copy, "field-bytes", 0);
    Module copyRewritten = Rewrite(copyOriginal);
    foreach (bool present in new[] { false, true })
    {
        string flag = Path.Combine(root, "flag");
        if (present) await File.WriteAllTextAsync(flag, "present"); else File.Delete(flag);
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(present ? "你好 Klee" : "other branch");
        foreach (Module program in new[] { copyOriginal, copyRewritten, RoundTrip(copyRewritten), RoundTrip(Binding.Bind(copyRewritten, new(root, root))) })
        {
            File.Delete(Path.Combine(root, "result"));
            await Interpreter.ExecuteAsync(program, new(WorkingDirectory: root));
            Check((await File.ReadAllBytesAsync(Path.Combine(root, "result"))).AsSpan().SequenceEqual(expected),
                "Cross-block field transfer changed actual UTF-8 bytes");
        }
    }

    var random = new Random(723911);
    for (var sample = 0; sample < 32; sample++)
    {
        int first = random.Next(-100, 100), left = random.Next(-100, 100), right = random.Next(-100, 100), increment = random.Next(-10, 10);
        string source = $"let box = scope {{ sum: {first}, other: 2 }}; let alias = box; if exists ./flag {{ alias::sum = {left}; }} else {{ box::sum = {right}; }}; for item in [1, 2, 3] {{ box::sum = box::sum + {increment}; }}; box::sum + box::other";
        Module original = Compiler.Compile(source, "field-trace-" + sample, 0);
        Module lowered = Rewrite(original);
        foreach (bool present in new[] { false, true })
        {
            string flag = Path.Combine(root, "flag");
            if (present) await File.WriteAllTextAsync(flag, "present"); else File.Delete(flag);
            string expected = await Execute(original);
            Check(await Execute(lowered) == expected, "Random alias/field trace differs");
            Check(await Execute(RoundTrip(Binding.Bind(Optimizer.Optimize(lowered, 2), new(root, root)))) == expected,
                "Random alias/field trace differs after optimized executable serialization");
        }
    }
    Console.WriteLine($"PASS: {checks} cross-CFG field, alias, loop, artifact and selected-error contracts");
}
finally { Directory.Delete(root, recursive: true); }
