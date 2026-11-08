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
    if (args.Contains("--mutable", StringComparer.Ordinal)) await MutableChecks();
    else
    {
        await SemanticChecks();
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
    await Differential("invalid-regex", Program([new StringValue("text")], new(OpCode.Constant, 0), new(OpCode.RegexMatch, Text: "["), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)));
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
        ("mutable-scope-escape", "fn make() { let object = scope { field: 1 }; object::field = 7; object }; make()::field"),
        ("mutable-loop", "let value = 0; for item in [1, 2, 3] { value = value + item; }; value"),
        ("nullable-constructor", "scope SomeType { remote: ssh://unused, new: fn { scope { filename?: null } } }; let object = SomeType::new(); object::filename = ~/.codex; object::filename")
    }) await Differential(name, Compiler.Compile(source, name, 0), expectedValue: name switch
    {
        "mutable-branch" or "mutable-capture" or "mutable-closure-write" => "Number:9",
        "mutable-loop" => "Number:6",
        "mutable-scope-escape" => "Number:7",
        _ => null
    });

    const string dead = "scope SomeType { remote: ssh://unused, new: fn { scope { filename?: null } } }; let object = SomeType::new(); object::filename = ~/.codex;";
    Module executable = Binding.Bind(Optimizer.Optimize(Compiler.Compile(dead, "dead-object", 0), 3), new(root, root));
    Require(executable.Functions.Count == 1 && executable.Functions[0].Instructions.All(i => i.Op is OpCode.Null or OpCode.Return), "Nonescaping mutated object survives executable optimization: " + string.Join(" | ", executable.Functions.Select(f => f.Name + ": " + string.Join(",", f.Instructions.Select(i => $"{i.Op}({i.A},{i.B},{i.Text})")))));
}
