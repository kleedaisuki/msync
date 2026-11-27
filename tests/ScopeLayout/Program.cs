using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--scope-crossing-only", StringComparer.Ordinal))
{
    await ScopeCrossingArmChecks.Run();
    return;
}

if (args.Contains("--member-dispatch-only", StringComparer.Ordinal))
{
    await MemberDispatchChecks.Run();
    return;
}

if (args.Contains("--guarded-scope-only", StringComparer.Ordinal))
{
    await GuardedScopeChecks.Run();
    return;
}

if (args.Contains("--field-places-only", StringComparer.Ordinal))
{
    await FieldPlaceChecks.Run();
    return;
}

if (args.Contains("--namespace-only", StringComparer.Ordinal))
{
    await NamespaceErasureChecks.Run();
    return;
}

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
await MemberDispatchChecks.Run();
await FieldPlaceChecks.Run();
await GuardedScopeChecks.Run();
await ScopeCrossingArmChecks.Run();
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

ScopeLayout layout = new(new[] { "first", ".codex", "cell" });
CellValue cell = new(new StringValue("before"));
ScopeValue value = new(layout, [Value.True, new NumberValue(3), cell]) { AllocationSite = 17 };
Check(ReferenceEquals(value.Members, value), "Object allocated a separate member-view object");
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
catch (MsyncException error) { Check(error.Message == "Object has no member 'missing'", "Missing field write diagnostic changed"); }
try { _ = ScopeLayout.Parse("a\0a"); throw new InvalidOperationException("Duplicate member layout succeeded"); }
catch (ArgumentException error) { Check(error.Message.Contains("same key", StringComparison.Ordinal), "Duplicate construction error changed"); }

ScopeLayout emptyLayout = ScopeLayout.Parse("");
ScopeValue emptyFirst = new(emptyLayout, Array.Empty<Value>());
ScopeValue emptySecond = new(emptyLayout, Array.Empty<Value>());
RangeValue emptyValues = new(new Value[] { emptyFirst, emptySecond, emptyFirst }, TypeRef.Scope);
RuntimeContext nativeContext = new(new(WorkingDirectory: Environment.CurrentDirectory));
static ValueTask<Value> NoCallback(Value target, IReadOnlyList<Value> arguments, CancellationToken token) =>
    throw new InvalidOperationException("Unexpected callback during Object collection lookup");
RangeValue unique = (RangeValue)await NativeRegistry.InvokeAsync("ranges.unique", [emptyValues], nativeContext, NoCallback);
int uniqueCount = 0;
await foreach (Value item in unique.Items) uniqueCount++;
Check(uniqueCount == 2, "Native unique merged two Object instances sharing empty storage");
Check(await NativeRegistry.InvokeAsync("ranges.contains", [new RangeValue(new Value[] { emptyFirst }, TypeRef.Scope), emptySecond], nativeContext, NoCallback)
    is BoolValue { Data: false }, "Native contains mistook a distinct empty Object for an alias");
Check(await NativeRegistry.InvokeAsync("ranges.contains", [new RangeValue(new Value[] { emptyFirst }, TypeRef.Scope), emptyFirst], nativeContext, NoCallback)
    is BoolValue { Data: true }, "Native contains lost the same Object alias");
HashSet<Value> identities = [emptyFirst, emptySecond, value];
int hash = value.GetHashCode();
value.SetField(0, Value.False);
value.SetMember("cell", new StringValue("mutated-again"));
Check(identities.Count == 3 && identities.Contains(value) && value.GetHashCode() == hash, "Field mutation changed Object hash identity");
Check(!value.Equals(mapped) && !emptyFirst.Equals(emptySecond) && emptyFirst.Equals((Value)emptyFirst), "Object identity follows payload/layout instead of its instance");

Module Factory() => new([new StringValue("original")],
[
    new FunctionCode("main", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.NewScope, 0, 39), new(OpCode.Return)], TypeRef.Scope)
    { CodeSignature = new([], 1, []) }
], OptimizationLevel: 0)
{
    CodeTypes = [new(TypeKind.String), new(TypeKind.Scope, ScopeLayouts: [0])],
    ScopeLayouts = [new([new("field", 0)])]
};
Module factory = Factory();
ExecutionSession session = new();
ScopeValue first = (ScopeValue)await session.ExecuteAsync(factory);
ScopeValue second = (ScopeValue)await session.ExecuteAsync(factory);
Check(!ReferenceEquals(first, second), "Repeated execution reused an instance");
first.SetMember("field", new StringValue("changed"));
Check(second.Members["field"] is StringValue { Data: "original" }, "Repeated execution shared field payload storage");
Check(second.AllocationSite == 39, "NewScope lost allocation-site provenance");

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "scope-layout-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach ((string name, string source, string expected) in new[]
    {
        ("constructor-cells", "fn make(value) { object { field: value, read: fn { field }, write: fn(newValue) { field = newValue; } } }; let first = make(\"first\"); let second = make(\"second\"); first::write(\"changed\"); if second::read() == \"second\" { first::read() } else { \"aliased\" }", "changed"),
        ("closure-field-alias", "let item = object { field: \"before\", read: fn { field } }; let alias = item; alias::field = \"after\"; item::read()", "after"),
        ("loop-instances", "let read = fn { \"none\" }; for item in [\"first\", \"second\", \"third\"] { let record = object { field: item, read: fn { field } }; read = record::read; }; read()", "third"),
        ("branch-layout", "let record = if exists ./selected { object { first: true, value: \"selected\" } } else { object { value: \"other\", first: false } }; record::value", "other"),
        ("nullable-field", "let record = object { field?: \"before\", read: fn { field } }; record::field = null; if record::read() is null { \"cleared\" } else { \"stale\" }", "cleared"),
        ("dot-member", "let record = object { .codex: \"dot\" }; record::.codex", "dot"),
        ("empty-instance-identity", "fn make() { object {} }; let first = make(); let second = make(); let count = 0; for object in $std::ranges::unique([first, second, first]) { count = count + 1; }; if count == 2 and not $std::ranges::contains([first], second) and $std::ranges::contains([first], first) { \"distinct\" } else { \"merged\" }", "distinct")
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
    Module repeated = Compiler.Compile("fn make(value) { object { field: value } }; object { first: make(\"first\"), second: make(\"second\") }", "layout-reuse", 0);
    ScopeValue pair = (ScopeValue)await Interpreter.ExecuteAsync(repeated, new(WorkingDirectory: root));
    ScopeValue left = (ScopeValue)pair.Members["first"];
    ScopeValue right = (ScopeValue)pair.Members["second"];
    Check(ReferenceEquals(left.Layout, right.Layout), "Repeated constructor execution rebuilt the immutable shape");
    Check(!ReferenceEquals(left, right), "Shape reuse merged distinct runtime instances");

    const string permuted = """
        let trace = 0;
        fn mark(value: Number) { global trace; trace = trace * 10 + value; value };
        let left = object { z: mark(1), a: mark(2) };
        let right = object { a: mark(3), z: mark(4) };
        left::a = 99;
        object { left: left, right: right, trace: trace }
        """;
    using MemoryStream typed = new();
    Msync.Hir.HirArtifact.WriteProduced(Compiler.Analyze(permuted, "permuted-scope"), typed);
    typed.Position = 0;
    var canonical = Msync.Hir.HirLowerer.Lower(Msync.Hir.HirArtifact.Read(typed), new(OptimizationLevel: 0)).Module;
    foreach (var image in new[] { canonical, RoundTrip(canonical), RoundTrip(Binding.Bind(canonical, new(root, root))) })
    {
        ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
        ScopeValue firstRow = (ScopeValue)result["left"], secondRow = (ScopeValue)result["right"];
        Check(!firstRow.Equals(secondRow)
            && firstRow.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(secondRow.Keys)
            && firstRow.Layout.Names.SequenceEqual(image.ScopeLayouts[firstRow.Layout.CodeLayoutId].Fields.Select(field => field.Name))
            && secondRow.Layout.Names.SequenceEqual(image.ScopeLayouts[secondRow.Layout.CodeLayoutId].Fields.Select(field => field.Name)),
            "Permuted declarations changed named membership, physical ordinals or independent instances");
        Check(result["trace"] is NumberValue { Data: 1234 }, "Physical layout changed initializer evaluation order");
        Check(firstRow["a"] is NumberValue { Data: 99 } && firstRow["z"] is NumberValue { Data: 1 }
            && secondRow["a"] is NumberValue { Data: 3 } && secondRow["z"] is NumberValue { Data: 4 },
            "Physical layout changed a field binding");
    }

    CompilerSession privateCompiler = new(optimization: 2);
    Module privateFrames = privateCompiler.Compile(
        "fn take_field(n: Number) { let row = object { x: n }; row::x }; take_field(37)");
    Check(privateFrames.Functions.Where(function => function.Name == "take_field"
        && function.ReturnType == TypeRef.Number).ToArray() is [var privateFrame]
        && privateFrame.Instructions.All(instruction => instruction.Op != OpCode.NewScope),
        "Stable REPL entry slots blocked scalar replacement in a private callable");
    Check(await new ExecutionSession().ExecuteAsync(privateFrames) is NumberValue { Data: 37 },
        "Private frame optimization changed the REPL result");
    Module places = Compiler.Compile("let record = object { field: \"before\" }; record::field = \"after\"; record::field", "source-places", 0);
    Instruction[] fieldAccesses = places.Functions.SelectMany(function => function.Instructions)
        .Where(instruction => instruction.Op is OpCode.LoadField or OpCode.StoreField).ToArray();
    Check(fieldAccesses is [{ Op: OpCode.StoreField } write, { Op: OpCode.LoadField } read] && write.A == read.A,
        "A write and read of the same known field did not share their portable projection");
    Module restored = RoundTrip(places);
    var projection = restored.FieldProjections[fieldAccesses[0].A];
    Check(projection is { Name: "field", Cases: [{ Ordinal: 0 }] }
        && restored.ScopeLayouts[projection.Cases[0].Layout].Fields[0].Name == "field",
        "Binary serialization changed a known layout's physical ordinal");
    Check(await Interpreter.ExecuteAsync(restored, new(WorkingDirectory: root)) is StringValue { Data: "after" },
        "Restored typed field place did not observe the write");
    Module endpoint = Compiler.Compile("$local @ ./field", "endpoint-qualification", 0);
    Check(await Interpreter.ExecuteAsync(RoundTrip(endpoint), new(WorkingDirectory: root)) is PathValue { Raw: "./field" },
        "Connection qualification changed across portable code serialization");
    Module process = new([new ProcessValue(0, "output", "error")],
        [new FunctionCode("main", 0, 0, [new(OpCode.Constant, 0),
            new(OpCode.NativeCall, 1, Text: "process.stdout"), new(OpCode.Return)], TypeRef.String)
        { CodeSignature = new([], 0, []) }],
        Stage: ArtifactStage.Executable, Context: new(root, root), OptimizationLevel: 0)
    { CodeTypes = [new(TypeKind.String)] };
    Check(await Interpreter.ExecuteAsync(process, new(WorkingDirectory: root)) is StringValue { Data: "output" },
        "A Process member lowered as a native operation did not observe stdout");
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
