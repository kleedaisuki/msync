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
    .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method => method.Name == "Optimize" && method.GetParameters().Length == 4);
MethodInfo analyzeMemory = typeof(Optimizer).Assembly.GetType("Msync.Core.MemoryEffects")!
    .GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!;
Module Rewrite(Module original)
{
    var literals = original.Constants.ToList();
    object summaries = analyzeMemory.Invoke(null, [original])!;
    return original with
    {
        Functions = original.Functions.Select(function => (FunctionCode)transform.Invoke(null, [original, function, literals, summaries])!).ToArray(),
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
        ,("readonly-helper", "fn inspect(value) { exists value::path }; let box = scope { path: ./flag, left: 7 }; if exists ./flag { inspect(box); } else { inspect(box); }; box::left + box::left", false)
        ,("partial-write", "fn update(value) { value::left = 11; }; let box = scope { left: 0, right: 23 }; if exists ./flag { update(box); }; box::right + box::left", false)
        ,("write-refresh", "fn update(value) { value::left = 11; }; let box = scope { left: 0, right: 23 }; update(box); box::left = 19; if exists ./flag { box::right = 29; }; box::left", false)
        ,("captured-readonly", "let box = scope { path: ./flag, left: 7 }; fn inspect() { exists box::path }; if exists ./flag { inspect(); }; box::left + box::left", false)
        ,("captured-partial-write", "let box = scope { left: 0, right: 23 }; fn update() { box::left = 11; }; if exists ./flag { update(); }; box::right + box::left", false)
        ,("captured-rebind", "let box = scope { item: 1 }; let original = box; let other = scope { item: 2 }; fn update() { box = other; }; if exists ./flag { update(); }; box::item + original::item", false)
        ,("returned-alias", "fn identity(value) { value }; let box = scope { item: 0 }; let alias = identity(box); if exists ./flag { alias::item = 9; }; box::item", false)
        ,("enumerated-write", "let box = scope { item: 1 }; let values = $std::ranges::transform([1], fn(item) { box::item = 7; item }); if exists ./flag { box::item = 2; }; for item in values { }; box::item", false)
        ,("nested-partial-write", "fn update(value) { value::child::left = 11; }; let child = scope { left: 0, right: 23 }; let box = scope { child: child, mode: true }; if exists ./flag { update(box); }; child::right + child::left", false)
        ,("path-helper", "fn inspect(value) { exists value::path }; let box = scope { path: ./flag, mode: true }; let seen = inspect(box); if exists ./flag { seen = inspect(box); }; box::mode", false)
    };
    foreach (var example in examples)
    {
        Console.WriteLine("CASE " + example.Name);
        Module original = Compiler.Compile(example.Source, example.Name, 0);
        Module rewritten = Rewrite(original);
        if (example.Name is "readonly-helper" or "captured-readonly")
            Check(rewritten.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.GetMember || instruction.Text != "left"),
                example.Name + ": readonly call invalidated unrelated cached field");
        if (example.Name is "partial-write" or "captured-partial-write")
        {
            Check(rewritten.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.GetMember || instruction.Text != "right"),
                example.Name + ": a disjoint write invalidated the right field");
            Check(rewritten.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.GetMember && instruction.Text == "left"),
                example.Name + ": modified field was forwarded from a stale version");
        }
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
            if (example.Name is "branch-phi" or "loop-carried-field" or "readonly-helper" or "partial-write" or "captured-readonly" or "enumerated-write")
                foreach (int level in new[] { 2, 3 })
                {
                    Module production = Optimizer.Optimize(original, level);
                    Check(await Execute(production) == expected, example.Name + $": actual O{level} product callsite differs");
                    if (example.Eliminate) Check(production.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.MakeScope or OpCode.GetMember or OpCode.SetMember)),
                        example.Name + $": actual O{level} product callsite did not scalarize across CFG");
                    if (example.Name is "readonly-helper" or "captured-readonly")
                        Check(production.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.GetMember || instruction.Text != "left"),
                            example.Name + $": actual O{level} product callsite did not forward readonly field");
                    if (example.Name == "partial-write")
                        Check(production.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.GetMember || instruction.Text != "right"),
                            example.Name + $": actual O{level} product callsite invalidated disjoint field");
                }
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

    Module rawCell = new([new NumberValue(1), new NumberValue(7)], [new("raw-cell", 0, 3,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.Pop),
         new(OpCode.LoadStorage, 0), new(OpCode.StoreLocal, 1), new(OpCode.LoadStorage, 1), new(OpCode.MakeScope, 1, Text: "item"),
         new(OpCode.StoreLocal, 2), new(OpCode.True), new(OpCode.JumpIfFalse, 16),
         new(OpCode.LoadLocal, 2), new(OpCode.Constant, 1), new(OpCode.SetMember, Text: "item"), new(OpCode.Pop), new(OpCode.Jump, 16),
         new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
    Check(await Execute(Rewrite(rawCell)) == await Execute(rawCell), "Raw field-cell alias was copied into independent scalar storage");

    Module replacedCell = new([new NumberValue(1), new NumberValue(2), new NumberValue(7)],
        [new("replace-owner", 0, 3,
            [new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.InitializeLocal, 0),
             new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 1), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1),
             new(OpCode.InitializeLocal, 2), new(OpCode.LoadLocal, 2), new(OpCode.Call, 0, 2), new(OpCode.Pop),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.SetMember, Text: "other"), new(OpCode.Pop),
             new(OpCode.LoadLocal, 1), new(OpCode.GetMember, Text: "item"), new(OpCode.Return)], TypeRef.Number),
         new("replace", 0, 1,
            [new(OpCode.Constant, 1), new(OpCode.MakeScope, 1, Text: "other"), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)],
            TypeRef.Null, CaptureCount: 1)], OptimizationLevel: 0);
    Check(await Execute(Rewrite(replacedCell)) == await Execute(replacedCell), "A re-bound captured slot updated another instance's cached field");

    foreach (bool selected in new[] { false, true })
    {
        Module nullableAlias = new([new NumberValue(1)],
            [new("nullable-owner", 0, 1,
                [new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.StoreLocal, 0), new(OpCode.MakeClosure, 1),
                 new(OpCode.LoadLocal, 0), new(selected ? OpCode.True : OpCode.False), new(OpCode.Call, 2, 2),
                 new(OpCode.GetMember, Text: "item"), new(OpCode.Return)], TypeRef.Any),
             new("maybe", 2, 2,
                [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 4), new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.Null), new(OpCode.Return)],
                TypeRef.Scope.WithNull, ParameterTypes: [TypeRef.Scope, TypeRef.Bool])], OptimizationLevel: 0);
        Check(await Execute(Rewrite(nullableAlias)) == await Execute(nullableAlias), "May-return alias erased the Null alternative and its selected error");
    }

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
