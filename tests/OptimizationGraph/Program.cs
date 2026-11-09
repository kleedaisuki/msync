using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "optimization-graph-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--singletons", StringComparer.Ordinal)) assertions += await SingletonChecks.Run(root);
    else if (args.Contains("--directional", StringComparer.Ordinal)) { assertions += DirectionalChecks.Run(); await DirectionalTransfers(); }
    else if (args.Contains("--mutable", StringComparer.Ordinal)) await MutableChecks();
    else
    {
        await SemanticChecks();
        await ValueNumberingChecks();
        if (!args.Contains("--semantic", StringComparer.Ordinal)) await EliminationChecks();
    }
    Console.WriteLine($"Optimization graph: {assertions} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }

Module Program(Value[] constants, params Instruction[] instructions) => new(constants,
    [new("main", 0, 4, instructions, TypeRef.Any)]);

Module RoundTrip(Module module)
{
    Artifact.Validate(module);
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    stream.Position = 0;
    return Artifact.Read(stream);
}

string Snapshot(Value value) => value switch
{
    NullValue => "null",
    BoolValue boolean => "Bool:" + boolean.Data,
    NumberValue number => "Number:" + number.Data.ToString("R", CultureInfo.InvariantCulture),
    StringValue text => "String:" + text.Data,
    PathValue path => $"Path:{path.Raw}:{path.NativeText}:{path.Connection}",
    ScopeValue scope => "Scope:" + string.Join(";", scope.Members.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + Snapshot(p.Value))),
    _ => value.Type + ":" + value
};

async Task<string> Outcome(Module module, string directory)
{
    using StringWriter errors = new();
    try
    {
        Value value = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: directory, Error: errors, Jobs: 4));
        return Snapshot(value) + "|stderr:" + errors;
    }
    catch (Exception exception)
    {
        return "throw:" + exception.GetType().FullName + ":" + exception.Message;
    }
}

async Task Differential(string name, Module source, bool writes = false, string? expectedValue = null)
{
    string directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    string expected = await Outcome(source with { OptimizationLevel = 0 }, directory);
    if (expectedValue is not null) Require(expected == expectedValue + "|stderr:", name + ": baseline violates language result: " + expected);
    Dictionary<string, byte[]>? bytes = writes ? Files(directory) : null;
    for (int level = 1; level <= 3; level++)
    {
        Module optimized;
        try { optimized = Optimizer.Optimize(source, level); }
        catch (Exception error) { throw new InvalidOperationException($"{name}, O{level}: optimizer failed", error); }
        foreach (Module candidate in new[] { RoundTrip(optimized), RoundTrip(Binding.Bind(optimized, new(directory, directory))) })
        {
            if (writes) foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            string actual = await Outcome(candidate, directory);
            Require(actual == expected, $"{name}, O{level}, {candidate.Stage}: {expected} != {actual}");
            if (bytes is not null)
            {
                Dictionary<string, byte[]> actualBytes = Files(directory);
                Require(bytes.Keys.Order(StringComparer.Ordinal).SequenceEqual(actualBytes.Keys.Order(StringComparer.Ordinal)), name + ": output filenames differ");
                foreach ((string file, byte[] data) in bytes) Require(actualBytes[file].AsSpan().SequenceEqual(data), name + ": output bytes differ: " + file);
            }
        }
    }
}

Dictionary<string, byte[]> Files(string directory) => Directory.EnumerateFiles(directory)
    .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal);

async Task SemanticChecks()
{
    foreach ((string name, string source) in new[]
    {
        ("branch-value", "let condition = true; let value = if condition { 37 } else { 9 }; value + 5"),
        ("nested-branch", "let left = 2; if left == 2 { if false { 11 } else { 17 } } else { 23 }"),
        ("scope-field", "let box = scope { item: 19, other: true }; box::item + 3"),
        ("closure", "let offset = 7; let add = fn(value) { offset + value }; add(13)"),
        ("returned-closure", "fn outer(value) { fn (other) { value + other } }; let add = outer(9); add(14)"),
        ("recursive", "fn count(value) { if value == 0 { 31 } else { count(value - 1) } }; count(8)"),
        ("shared-callee", "fn twice(value) { value * 2 }; twice(3) + twice(5)"),
        ("range-loop", "for file in [] { file -> ./never }; 43"),
        ("unselected-effect", "if false { \"wrong\" -> ./forbidden } else { true }"),
        ("native-lexical", "let value = $std::fs::filename(./leaf); value")
    }) await Differential(name, Compiler.Compile(source, name, 0));

    foreach (bool left in new[] { false, true })
    foreach (bool right in new[] { false, true })
    foreach (bool conjunction in new[] { false, true })
    {
        string op = conjunction ? "and" : "or";
        string source = $"fn combine(left, right) {{ left {op} right }}; combine({left.ToString().ToLowerInvariant()}, {right.ToString().ToLowerInvariant()})";
        await Differential($"shortcircuit-{op}-{left}-{right}", Compiler.Compile(source, "shortcircuit", 0),
            expectedValue: "Bool:" + (conjunction ? left && right : left || right));
    }

    await Differential("transfer-chain", Compiler.Compile("\"Klee\\n世界\" -> ./a; ./a -> ./b; ./b -> ./c", "transfer-chain", 0), writes: true);
    await Differential("call-transfer", Compiler.Compile("fn write(value, destination) { value -> destination }; write(\"actual bytes\", ./result)", "call-transfer", 0), writes: true);

    // These are valid bytecode inputs whose selected operation throws. Removing
    // a discarded value must not erase the operation's runtime diagnostic.
    await Differential("missing-member", Program([], new(OpCode.MakeScope, 0, Text: ""), new(OpCode.GetMember, Text: "missing"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)));
    await Differential("duplicate-scope", Program([], new(OpCode.True), new(OpCode.False), new(OpCode.MakeScope, 2, Text: "field\0field"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)));
    await Differential("duplicate-large-scope", Program([], new(OpCode.True), new(OpCode.True), new(OpCode.True), new(OpCode.True), new(OpCode.True),
        new(OpCode.True), new(OpCode.MakeScope, 6, Text: "a\0b\0c\0d\0e\0a"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)));
    await Differential("unselected-duplicate-scope", Program([], new(OpCode.False), new(OpCode.JumpIfFalse, 7), new(OpCode.True), new(OpCode.False),
        new(OpCode.MakeScope, 2, Text: "field\0field"), new(OpCode.Pop), new(OpCode.Jump, 7), new(OpCode.Null), new(OpCode.Return)));
    await Differential("duplicate-scope-callee", new Module([new FunctionValue(1)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Call, 0, 2), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
            new("bad", 0, 0, [new(OpCode.True), new(OpCode.False), new(OpCode.MakeScope, 2, Text: "field\0field"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)], OptimizationLevel: 0));
    await Differential("constant-closure-mutable-capture", new Module(
        [new FunctionValue(1, [new ScopeValue(new Dictionary<string, Value> { ["count"] = new NumberValue(0) })]), new NumberValue(1)],
        [new("main", 0, 1, [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Call, 0, 2),
            new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Number),
            new("increment", 0, 2, [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "count"), new(OpCode.Constant, 1), new(OpCode.Add),
                new(OpCode.InitializeLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.SetMember, Text: "count"),
                new(OpCode.Pop), new(OpCode.LoadLocal, 1), new(OpCode.Return)], TypeRef.Number, CaptureCount: 1)], OptimizationLevel: 0), expectedValue: "Number:2");
    await Differential("specialized-frame-capacity", new Module([new FunctionValue(2, [Value.True, Value.False, Value.Null])],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Null),
            new("small", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
            new("captured", 0, 3, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 3)], OptimizationLevel: 0));
    await Differential("invalid-regex", Program([new StringValue("text")], new(OpCode.Constant, 0), new(OpCode.RegexMatch, Text: "["), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)));
    await Differential("ieee-signed-zero", Program([new NumberValue(1), new NumberValue(0)],
        new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Divide),
        new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Negate), new(OpCode.Divide),
        new(OpCode.MakeScope, 2, Text: "positive\0negative"), new(OpCode.Return)),
        expectedValue: "Scope:negative=Number:-Infinity;positive=Number:Infinity");
    await Differential("raw-cell-alias", new Module([], [new("alias", 0, 2,
        [new(OpCode.True), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.InitializeLocal, 1),
         new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 10), new(OpCode.False), new(OpCode.StoreLocal, 1),
         new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.False), new(OpCode.Return)], TypeRef.Bool)], OptimizationLevel: 0),
        expectedValue: "Bool:False");
    await Differential("ieee-division", Program([new NumberValue(1), new NumberValue(0)], new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Divide), new(OpCode.Return)));

    Module metadata = Compiler.Compile("exists ./late-file", "late-filesystem", 0);
    string late = Path.Combine(root, "late-file");
    foreach (int level in new[] { 1, 2, 3 })
    {
        Module bound = RoundTrip(Binding.Bind(Optimizer.Optimize(metadata, level), new(root, root)));
        await File.WriteAllTextAsync(late, "created after executable lowering");
        Require(await Outcome(bound, root) == "Bool:True|stderr:", "Bound artifact froze a missing-file observation");
        File.Delete(late);
        Require(await Outcome(bound, root) == "Bool:False|stderr:", "Bound artifact retained a stale filesystem observation");
    }

    Random random = new(81372);
    for (int sample = 0; sample < 64; sample++)
    {
        int a = random.Next(-50, 51), b = random.Next(-50, 51), c = random.Next(1, 11);
        string source = $"let x = {a}; let y = {b}; let select = x < y; if select {{ (x + y) * {c} }} else {{ (x - y) * {c} }}";
        await Differential("generated-" + sample, Compiler.Compile(source, "generated", 0), expectedValue: "Number:" + ((a < b ? a + b : a - b) * c).ToString(CultureInfo.InvariantCulture));
    }
}

async Task EliminationChecks()
{
    foreach ((string name, string source) in new[]
    {
        ("dead-scalar", "let first = 3; let second = first + 5;"),
        ("dead-scope", "let item = scope { field: 3, remote: ssh://unused };"),
        ("dead-constructor", "scope SomeType { remote: ssh://unused, new: fn { scope { filename: ./unused } } }; let object = SomeType::new();"),
        ("dead-function", "fn unused(value) { value + 1 }; let value = 12;")
    })
    {
        Module portable = Compiler.Compile(source, name, 0);
        Module executable = Binding.Bind(Optimizer.Optimize(portable, 3), new(root, root));
        await Differential(name, portable);
        Require(executable.Functions.Count == 1, name + ": executable retains unreachable functions");
        Require(executable.Functions[0].Instructions.All(i => i.Op is OpCode.Null or OpCode.Return), name + ": executable retains dead computation: " + string.Join(",", executable.Functions[0].Instructions.Select(i => i.Op)));
        Require(executable.Constants.Count == 0, name + ": executable retains unreachable constants");
    }
}

async Task MutableChecks()
{
    foreach ((string name, string source) in new[]
    {
        ("mutable-branch", "let value = 2; if true { value = 9; }; value"),
        ("mutable-alias", "let object = scope { filename?: null }; let alias = object; alias::filename = ./chosen; object::filename"),
        ("mutable-capture", "let value = 2; let read = fn { value }; value = 9; read()"),
        ("mutable-callee-alias", "fn set(object) { object::filename = ./chosen; }; let object = scope { filename?: null }; set(object); object::filename"),
        ("mutable-closure-write", "let value = 2; let set = fn { value = 9; }; set(); value"),
        ("mutable-callee-argument-order", "let action = fn(value: Bool) { \"A\" }; let replacement = fn(value: Bool) { \"B\" }; fn change() { action = replacement; true }; let first = action(change()); let second = action(false); $std::string::concat(first, second)"),
        ("mutable-scope-escape", "fn make() { let object = scope { field: 1 }; object::field = 7; object }; make()::field"),
        ("mutable-escaped-field-storage", "let getter = fn { 0 }; let object = scope { value: 1, hook: { getter = fn { value }; true } }; object::value = 2; getter()"),
        ("mutable-loop", "let value = 0; for item in [1, 2, 3] { value = value + item; }; value"),
        ("nullable-constructor", "scope SomeType { remote: ssh://unused, new: fn { scope { filename?: null } } }; let object = SomeType::new(); object::filename = ~/.codex; object::filename")
    }) await Differential(name, Compiler.Compile(source, name, 0), expectedValue: name switch
    {
        "mutable-branch" or "mutable-capture" or "mutable-closure-write" => "Number:9",
        "mutable-loop" => "Number:6",
        "mutable-escaped-field-storage" => "Number:2",
        "mutable-scope-escape" => "Number:7",
        "mutable-callee-argument-order" => "String:AB",
        _ => null
    });

    await Differential("mutable-callee-fresh-location", new Module(
        [new FunctionValue(1), new FunctionValue(2), new FunctionValue(3), new StringValue("A"), new StringValue("B"), new StringValue("C")],
        [new("main", 0, 2, [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 2), new(OpCode.StoreLocal, 1),
            new(OpCode.LoadLocal, 0), new(OpCode.Call, 0), new(OpCode.Return)], TypeRef.String),
            new("a", 0, 0, [new(OpCode.Constant, 3), new(OpCode.Return)], TypeRef.String),
            new("b", 0, 0, [new(OpCode.Constant, 4), new(OpCode.Return)], TypeRef.String),
            new("c", 0, 0, [new(OpCode.Constant, 5), new(OpCode.Return)], TypeRef.String)], OptimizationLevel: 0), expectedValue: "String:B");
    Module direct = Optimizer.Optimize(Compiler.Compile("let action = fn { 42 }; action()", "direct-target", 0), 1);
    Require(direct.Functions.Count == 1 && direct.Functions[0].Instructions.All(instruction => instruction.Op is OpCode.Constant or OpCode.Return),
        "Ordinary unaliased call target lost its inlining proof");

    const string dead = "scope SomeType { remote: ssh://unused, new: fn { scope { filename?: null } } }; let object = SomeType::new(); object::filename = ~/.codex;";
    Module executable = Binding.Bind(Optimizer.Optimize(Compiler.Compile(dead, "dead-object", 0), 3), new(root, root));
    Require(executable.Functions.Count == 1 && executable.Functions[0].Instructions.All(i => i.Op is OpCode.Null or OpCode.Return), "Nonescaping mutated object survives executable optimization: " + string.Join(" | ", executable.Functions.Select(f => f.Name + ": " + string.Join(",", f.Instructions.Select(i => $"{i.Op}({i.A},{i.B},{i.Text})")))));
}


async Task ValueNumberingChecks()
{
    const string environmentName = "MSYNC_OPT_GRAPH_INPUT";
    string? previous = Environment.GetEnvironmentVariable(environmentName);
    Environment.SetEnvironmentVariable(environmentName, "runtime text");
    try
    {
        foreach ((string name, string source, OpCode operation, string expected) in new[]
        {
            ("cse-bool", "let value = exists ./absent; scope { first: not value, second: not value }", OpCode.Not, "Scope:first=Bool:True;second=Bool:True"),
            ("cse-number", "let value = if exists ./absent { 3 } else { 5 }; scope { first: - value, second: - value }", OpCode.Negate, "Scope:first=Number:-5;second=Number:-5"),
            ("cse-string", "native fn string.concat(left: String, right: String) -> String; let value: String = $env::MSYNC_OPT_GRAPH_INPUT; scope { first: string.concat(value, \"x\"), second: string.concat(value, \"x\") }", OpCode.NativeCall, "Scope:first=String:runtime textx;second=String:runtime textx")
        })
        {
            Module original = Compiler.Compile(source, name, 0);
            await Differential(name, original, expectedValue: expected);
            for (int level = 1; level <= 3; level++)
            {
                Module optimized = Optimizer.Optimize(original, level);
                Require(optimized.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == operation)) == 1,
                    $"{name} O{level}: repeated runtime-unknown operation was not eliminated: " + string.Join(" | ", optimized.Functions.Select(f => f.Name + ": " + string.Join(",", f.Instructions.Select(i => $"{i.Op}({i.Text?.Replace("\0", "|")})")))));
            }
        }
    }
    finally { Environment.SetEnvironmentVariable(environmentName, previous); }

    await Differential("cse-observation", Compiler.Compile(
        "let first = exists ./flag; \"bytes\" -> ./flag; let second = exists ./flag; scope { first: first, second: second }", "cse-observation", 0), writes: true,
        expectedValue: "Scope:first=Bool:False;second=Bool:True");

    await Differential("cse-scope-write", Compiler.Compile(
        "let object = scope { value: exists ./absent }; let first = not object::value; object::value = true; let second = not object::value; scope { first: first, second: second }", "cse-scope-write", 0),
        expectedValue: "Scope:first=Bool:True;second=Bool:False");
    await Differential("cse-capture-write", Compiler.Compile(
        "let value = exists ./absent; let read = fn { not value }; let first = read(); value = true; let second = read(); scope { first: first, second: second }", "cse-capture-write", 0),
        expectedValue: "Scope:first=Bool:True;second=Bool:False");

    // An empty operand stack at Return supplies Null. A stack
    // splice must materialize Null rather than stealing a caller operand.
    Module emptyReturn = new([new NumberValue(7)],
    [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1), new(OpCode.Call), new(OpCode.Pop), new(OpCode.Return)], TypeRef.Number),
     new("empty-return", 0, 0, [new(OpCode.Return)], TypeRef.Null)]);
    await Differential("empty-return", emptyReturn, expectedValue: "Number:7");
}


async Task DirectionalTransfers()
{
    int sample = 0;
    foreach (int[] events in new int[][] { [0, 1, 0, 2, 0], [1, 0, 2, 0, 1, 0], [0, 2, 0, 1, 0], [2, 1, 0, 1, 2, 0] })
    {
        List<Value> constants = [new PathValue("output"), new PathValue("observed")];
        List<Instruction> instructions = [];
        int transfer = 0;
        foreach (int item in events)
        {
            if (item != 0) { instructions.Add(new(OpCode.Barrier, item)); continue; }
            int literal = constants.Count;
            constants.Add(new StringValue("bytes-" + transfer++));
            instructions.Add(new(OpCode.Constant, literal)); instructions.Add(new(OpCode.Constant, 0));
            instructions.Add(new(OpCode.Transfer)); instructions.Add(new(OpCode.Pop));
        }
        instructions.Add(new(OpCode.Constant, 0)); instructions.Add(new(OpCode.Constant, 1));
        instructions.Add(new(OpCode.Transfer)); instructions.Add(new(OpCode.Pop));
        instructions.Add(new(OpCode.Null)); instructions.Add(new(OpCode.Return));
        await Differential("directional-bytes-" + sample++, new Module(constants,
            [new("directional-bytes", 0, 0, instructions, TypeRef.Null)], OptimizationLevel: 0), writes: true, expectedValue: "null");
    }
}
