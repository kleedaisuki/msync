using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--call-preparation-only", StringComparer.Ordinal))
{
    await CallPreparationChecks.Run();
    return;
}

if (args.Contains("--duplicate-only", StringComparer.Ordinal))
{
    await DuplicateConstructionChecks.Run();
    return;
}

if (args.Contains("--packed-isolation-only", StringComparer.Ordinal))
{
    SpatialLayoutChecks.CheckEscapedRowIsolation();
    return;
}

if (args.Contains("--spatial-vm-only", StringComparer.Ordinal))
{
    await SpatialVmChecks.Run();
    return;
}

if (args.Contains("--spatial-only", StringComparer.Ordinal)
    || args.Contains("--packed-spatial-only", StringComparer.Ordinal))
{
    SpatialLayoutChecks.Run(args.Contains("--packed-spatial-only", StringComparer.Ordinal));
    return;
}

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

ScopeLayout layout = new(new[] { "first", ".codex", "cell" });
CellValue cell = new(new StringValue("before"));
ScopeValue value = new(layout, [Value.True, new NumberValue(3), cell]) { AllocationSite = 17 };
Check(ReferenceEquals(value.Members, value), "Scope allocated a separate member-view object");
Check(value.IsField(1, ".codex") && !value.IsField(-1, ".codex") && !value.IsField(1, "cell"), "Offset/name guard changed");
Check(value.TryGetFieldOffset("cell", out int offset) && offset == 2, "Field offset lookup changed");
Check(value.GetField(2) is StringValue { Data: "before" }, "Indexed read did not dereference shared storage");
value.SetField(2, new StringValue("indexed"));
Check(cell.Data is StringValue { Data: "indexed" }, "Indexed write replaced the captured cell");
value.SetMember("cell", new StringValue("named"));
Check(value.Members["cell"] is StringValue { Data: "named" } && cell.Data is StringValue { Data: "named" }, "Named/member reads no longer share storage");
Check(!value.Members.TryGetValue("missing", out Value? absent) && absent is NullValue, "Missing member lookup changed");
Check(value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(layout.Names), "Member enumeration lost a name");
Check(value.Single(field => field.Key == "cell").Value is StringValue { Data: "named" }, "Value enumeration exposed a raw Cell");
ScopeValue mapped = value.MapFields(item => item);
Check(ReferenceEquals(mapped.Layout, layout) && mapped.AllocationSite == 17, "Cloning lost immutable layout or source provenance");
mapped.SetMember("cell", new StringValue("independent"));
Check(cell.Data is StringValue { Data: "named" }, "Cloning the visible values retained old mutable cells");
try { value.SetMember("missing", Value.Null); throw new InvalidOperationException("Missing field write succeeded"); }
catch (MsyncException error) { Check(error.Message == "Scope has no member 'missing'", "Missing field write diagnostic changed"); }
try { _ = ScopeLayout.Parse("a\0a"); throw new InvalidOperationException("Duplicate member layout succeeded"); }
catch (ArgumentException error) { Check(error.Message.Contains("same key", StringComparison.Ordinal), "Duplicate construction error changed"); }

ScopeLayout emptyLayout = ScopeLayout.Parse("");
ScopeValue emptyFirst = new(emptyLayout, Array.Empty<Value>());
ScopeValue emptySecond = new(emptyLayout, Array.Empty<Value>());
RangeValue emptyValues = new(new Value[] { emptyFirst, emptySecond, emptyFirst }, TypeRef.Scope);
RuntimeContext nativeContext = new(new(WorkingDirectory: Environment.CurrentDirectory));
static ValueTask<Value> NoCallback(Value target, IReadOnlyList<Value> arguments, CancellationToken token) =>
    throw new InvalidOperationException("Unexpected callback during Scope collection lookup");
RangeValue unique = (RangeValue)await NativeRegistry.InvokeAsync("ranges.unique", [emptyValues], nativeContext, NoCallback);
int uniqueCount = 0;
await foreach (Value item in unique.Items) uniqueCount++;
Check(uniqueCount == 2, "Native unique merged two Scope instances sharing empty storage");
Check(await NativeRegistry.InvokeAsync("ranges.contains", [new RangeValue(new Value[] { emptyFirst }, TypeRef.Scope), emptySecond], nativeContext, NoCallback)
    is BoolValue { Data: false }, "Native contains mistook a distinct empty Scope for an alias");
Check(await NativeRegistry.InvokeAsync("ranges.contains", [new RangeValue(new Value[] { emptyFirst }, TypeRef.Scope), emptyFirst], nativeContext, NoCallback)
    is BoolValue { Data: true }, "Native contains lost the same Scope alias");
HashSet<Value> identities = [emptyFirst, emptySecond, value];
int hash = value.GetHashCode();
value.SetField(0, Value.False);
value.SetMember("cell", new StringValue("mutated-again"));
Check(identities.Count == 3 && identities.Contains(value) && value.GetHashCode() == hash, "Field mutation changed Scope hash identity");
Check(!value.Equals(mapped) && !emptyFirst.Equals(emptySecond) && emptyFirst.Equals((Value)emptyFirst), "Scope identity follows payload/layout instead of its instance");

Module Factory() => new([new StringValue("original")],
[
    new("main", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, 39, "field"), new(OpCode.Return)], TypeRef.Scope)
], OptimizationLevel: 0);
Module factory = Factory();
ExecutionSession session = new();
ScopeValue first = (ScopeValue)await session.ExecuteAsync(factory);
ScopeValue second = (ScopeValue)await session.ExecuteAsync(factory);
Check(!ReferenceEquals(first, second), "Repeated execution reused an instance");
first.SetMember("field", new StringValue("changed"));
Check(second.Members["field"] is StringValue { Data: "original" }, "Repeated execution shared field payload storage");
Check(second.AllocationSite == 39, "MakeScope lost allocation-site provenance");

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "scope-layout-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach ((string name, string source, string expected) in new[]
    {
        ("constructor-cells", "fn make(value) { scope { field: value, read: fn { field }, write: fn(newValue) { field = newValue; } } }; let first = make(\"first\"); let second = make(\"second\"); first::write(\"changed\"); if second::read() == \"second\" { first::read() } else { \"aliased\" }", "changed"),
        ("closure-field-alias", "let item = scope { field: \"before\", read: fn { field } }; let alias = item; alias::field = \"after\"; item::read()", "after"),
        ("loop-instances", "let read = fn { \"none\" }; for item in [\"first\", \"second\", \"third\"] { let object = scope { field: item, read: fn { field } }; read = object::read; }; read()", "third"),
        ("branch-layout", "let object = if exists ./selected { scope { first: true, value: \"selected\" } } else { scope { value: \"other\", first: false } }; object::value", "other"),
        ("nullable-field", "let object = scope { field?: \"before\", read: fn { field } }; object::field = null; if object::read() is null { \"cleared\" } else { \"stale\" }", "cleared"),
        ("dot-member", "let object = scope { .codex: \"dot\" }; object::.codex", "dot"),
        ("empty-instance-identity", "fn make() { scope {} }; let first = make(); let second = make(); let count = 0; for object in $std::ranges::unique([first, second, first]) { count = count + 1; }; if count == 2 and not $std::ranges::contains([first], second) and $std::ranges::contains([first], first) { \"distinct\" } else { \"merged\" }", "distinct")
    })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module raw = Compiler.Compile(source, name, level);
            foreach (Module program in new[] { raw, RoundTrip(raw), RoundTrip(Binding.Bind(raw, new(root, root))) })
            {
                Value actual = await Interpreter.ExecuteAsync(program, new(WorkingDirectory: root));
                Check(actual is StringValue text && text.Data == expected, $"{name} O{level}: expected '{expected}', received '{actual}'");
            }
        }
    }
    Module repeated = Compiler.Compile("fn make(value) { scope { field: value } }; scope { first: make(\"first\"), second: make(\"second\") }", "layout-reuse", 0);
    ScopeValue pair = (ScopeValue)await Interpreter.ExecuteAsync(repeated, new(WorkingDirectory: root));
    ScopeValue left = (ScopeValue)pair.Members["first"];
    ScopeValue right = (ScopeValue)pair.Members["second"];
    Check(ReferenceEquals(left.Layout, right.Layout), "Repeated constructor execution rebuilt the immutable shape");
    Check(!ReferenceEquals(left, right), "Shape reuse merged distinct runtime instances");

    const string permuted = """
        let trace = 0;
        fn mark(value: Number) { global trace; trace = trace * 10 + value; value };
        let left = scope { z: mark(1), a: mark(2) };
        let right = scope { a: mark(3), z: mark(4) };
        left::a = 99;
        scope { left: left, right: right, trace: trace }
        """;
    using MemoryStream typed = new();
    Msync.Hir.HirArtifact.WriteProduced(Compiler.Analyze(permuted, "permuted-scope"), typed);
    typed.Position = 0;
    var canonical = Msync.Hir.HirLowerer.Lower(Msync.Hir.HirArtifact.Read(typed), new(OptimizationLevel: 0)).Module;
    foreach (var image in new[] { canonical, RoundTrip(canonical), RoundTrip(Binding.Bind(canonical, new(root, root))) })
    {
        ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
        ScopeValue firstRow = (ScopeValue)result["left"], secondRow = (ScopeValue)result["right"];
        Check(ReferenceEquals(firstRow.Layout, secondRow.Layout) && !firstRow.Equals(secondRow),
            "Permuted field declarations did not share one layout with independent instances");
        Check(result["trace"] is NumberValue { Data: 1234 }, "Physical layout changed initializer evaluation order");
        Check(firstRow["a"] is NumberValue { Data: 99 } && firstRow["z"] is NumberValue { Data: 1 }
            && secondRow["a"] is NumberValue { Data: 3 } && secondRow["z"] is NumberValue { Data: 4 },
            "Physical layout changed a field binding");
    }

    CompilerSession privateCompiler = new(optimization: 2);
    Module privateFrames = privateCompiler.Compile(
        "fn take_field(n: Number) { let row = scope { x: n }; row::x }; take_field(37)");
    Check(privateFrames.Functions.Where(function => function.Name == "take_field"
        && function.ReturnType == TypeRef.Number).ToArray() is [var privateFrame]
        && privateFrame.Instructions.All(instruction => instruction.Op != OpCode.MakeScope),
        "Stable REPL entry slots blocked scalar replacement in a private callable");
    Check(await new ExecutionSession().ExecuteAsync(privateFrames) is NumberValue { Data: 37 },
        "Private frame optimization changed the REPL result");
    Module sourceHints = Compiler.Compile("let object = scope { field: \"before\" }; object::field = \"after\"; object::field", "source-hints", 0);
    Instruction[] fieldAccesses = sourceHints.Functions.SelectMany(function => function.Instructions)
        .Where(instruction => instruction.Op is OpCode.GetMember or OpCode.SetMember).ToArray();
    Check(fieldAccesses.Length == 2 && fieldAccesses.All(instruction => instruction.A > 0), "Known field layout did not provide compiler offset hints");
    Check(RoundTrip(sourceHints).Functions[0].Instructions.SequenceEqual(sourceHints.Functions[0].Instructions), "Binary serialization changed field hints");
    Module ignoredHints = sourceHints with
    {
        Functions = sourceHints.Functions.Select(function => function with
        {
            Instructions = function.Instructions.Select(instruction => instruction.Op is OpCode.GetMember or OpCode.SetMember
                ? instruction with { A = 0 } : instruction).ToArray()
        }).ToArray()
    };
    Check(await Interpreter.ExecuteAsync(ignoredHints, new(WorkingDirectory: root)) is StringValue { Data: "after" }, "Ignoring offset hints changed named access semantics");
    foreach (int hint in new[] { 0, 1, 2, 19, -4 })
    {
        Module hinted = new([new StringValue("left"), new StringValue("right"), new StringValue("changed")],
            [new("main", 0, 1,
            [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.MakeScope, 2, Text: "first\0second"), new(OpCode.StoreLocal, 0),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.SetMember, hint, Text: "second"), new(OpCode.Pop),
             new(OpCode.LoadLocal, 0), new(OpCode.GetMember, hint, Text: "second"), new(OpCode.Return)], TypeRef.String)], OptimizationLevel: 0);
        foreach (Module program in new[] { hinted, RoundTrip(hinted), RoundTrip(Binding.Bind(hinted, new(root, root))) })
            Check(await Interpreter.ExecuteAsync(program, new(WorkingDirectory: root)) is StringValue { Data: "changed" }, $"Offset hint {hint} bypassed named-field semantics");
    }
    Module endpoint = new([new ConnectionValue(ConnectionKind.Local)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.GetMember, 99, Text: "field"), new(OpCode.Return)], TypeRef.Path)], OptimizationLevel: 0);
    Check(await Interpreter.ExecuteAsync(RoundTrip(endpoint), new(WorkingDirectory: root)) is PathValue { Raw: "field" }, "A field hint changed Connection qualification");
    Module process = new([new ProcessValue(0, "output", "error")],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.GetMember, 99, Text: "stdout"), new(OpCode.Return)], TypeRef.String)],
        Stage: ArtifactStage.Executable, Context: new(root, root), OptimizationLevel: 0);
    Check(await Interpreter.ExecuteAsync(process, new(WorkingDirectory: root)) is StringValue { Data: "output" }, "A field hint changed Process member selection");
    const string lazyDuplicate = "a\0a";
    foreach (bool selected in new[] { false, true })
    {
        Module duplicate = new([], [new("main", 0, 0,
            [new(selected ? OpCode.True : OpCode.False), new(OpCode.JumpIfFalse, 7), new(OpCode.True), new(OpCode.False),
             new(OpCode.MakeScope, 2, Text: lazyDuplicate), new(OpCode.Pop), new(OpCode.Jump, 7), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)], OptimizationLevel: 0);
        try
        {
            _ = await Interpreter.ExecuteAsync(duplicate, new(WorkingDirectory: root));
            Check(!selected, "A selected duplicate field constructor succeeded");
        }
        catch (MsyncException error)
        {
            Check(selected && error.InnerException is ArgumentException cause && cause.Message.Contains("same key", StringComparison.Ordinal),
                $"Duplicate selection {selected}: {error}");
        }
    }
}
finally { Directory.Delete(root, recursive: true); }

Console.WriteLine($"PASS: {assertions} compact-layout/member/cell/source/object/executable contracts");
if (args.Contains("--performance", StringComparer.Ordinal)) Performance.Run();

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}
