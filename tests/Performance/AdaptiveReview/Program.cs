using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args is ["--review-child", var childMarker])
{
    File.WriteAllText(childMarker, "unexpected process invocation");
    return;
}
if (args.Contains("--source-order", StringComparer.Ordinal))
{
    await SourceOrderingChecks.Run();
    return;
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "adaptive-independent-review", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
Module Program(IReadOnlyList<Value> constants, int locals, IReadOnlyList<Instruction> code, params FunctionCode[] functions) =>
    new(constants, [new("main", 0, locals, code, TypeRef.Any), .. functions], Stage: ArtifactStage.Executable,
        Context: new(root, root), OptimizationLevel: 2);
async Task<Value> Run(Module module)
{
    Artifact.Validate(module);
    return await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null));
}
Module Codec(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream); stream.Position = 0;
    return Artifact.Read(stream);
}
string Text(Value value) => value is StringValue text ? text.Data : throw new InvalidOperationException("Expected String, got " + value.Type);
try
{
    // Both closures execute exactly the same body, but own different locations.
    Module captures = Program([new StringValue("first"), new StringValue("second"), new StringValue("mutated")], 6,
    [
        new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.StoreLocal, 1),
        new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.StoreLocal, 2),
        new(OpCode.LoadLocal, 1), new(OpCode.Call, 0, 2), new(OpCode.StoreLocal, 3),
        new(OpCode.LoadLocal, 2), new(OpCode.Call, 0, 2), new(OpCode.StoreLocal, 4),
        new(OpCode.Constant, 2), new(OpCode.StoreLocal, 0),
        new(OpCode.LoadLocal, 2), new(OpCode.Call, 0, Text: "1:1"), new(OpCode.StoreLocal, 5),
        new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 5),
        new(OpCode.MakeScope, 3, Text: "first\0second\0mutated"), new(OpCode.Return)
    ], new FunctionCode("read", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String, CaptureCount: 1));
    foreach (Module module in new[] { captures, Codec(captures) })
    {
        ScopeValue result = (ScopeValue)await Run(module);
        Check(Text(result.Members["first"]) == "first" && Text(result.Members["second"]) == "second"
            && Text(result.Members["mutated"]) == "mutated", "A selected code target retained an old closure/cell");
    }

    Module ordering = Program([new StringValue("A"), new StringValue("B")], 3,
    [
        new(OpCode.MakeClosure, 1), new(OpCode.StoreLocal, 0),
        new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 3, 1), new(OpCode.StoreLocal, 1),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Call, 0, 4),
        new(OpCode.Call, 1, Text: "1:1;2:2"), new(OpCode.StoreLocal, 2),
        new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 0), new(OpCode.Null),
        new(OpCode.Call, 1, Text: "1:1;2:2"), new(OpCode.MakeScope, 2, Text: "first\0second"), new(OpCode.Return)
    ],
        new FunctionCode("A", 1, 1, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.String),
        new FunctionCode("B", 1, 1, [new(OpCode.Constant, 1), new(OpCode.Return)], TypeRef.String),
        new FunctionCode("swap", 0, 1, [new(OpCode.MakeClosure, 2), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1));
    foreach (Module module in new[] { ordering, Codec(ordering) })
    {
        ScopeValue result = (ScopeValue)await Run(module);
        Check(Text(result.Members["first"]) == "A" && Text(result.Members["second"]) == "B",
            "Argument evaluation retargeted the already evaluated callee");
    }

    foreach (Instruction call in new[] { new Instruction(OpCode.Call, 1, 2), new Instruction(OpCode.Call, 1, Text: "2:1") })
    {
        Module native = Program([new FunctionValue(8, NativeName: "bool.negate")], 0,
            [new(OpCode.Constant, 0), new(OpCode.True), call, new(OpCode.Return)],
            new FunctionCode("not-the-native", 1, 1, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool]),
            new FunctionCode("table-key", 1, 1, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool]));
        Check(await Run(native) is BoolValue { Data: false }, "A specialization table/target replaced actual native identity");
    }

    string marker = Path.Combine(root, "must-not-launch");
    Module badCallee = Program([new PathValue(Environment.ProcessPath!), new StringValue("--review-child"), new PathValue(marker)], 0,
        [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Call, 2, 2), new(OpCode.Return)],
        new FunctionCode("hint-is-not-callee-proof", 2, 2, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, ParameterTypes: [TypeRef.String, TypeRef.Path]));
    bool failed = false;
    try { await Run(badCallee); }
    catch (Exception error) when (error is MsyncException or InvalidCastException) { failed = true; }
    Check(failed && !File.Exists(marker), "Ignoring an invalid function override launched a process");

    // All reads are the same function/PC; layouts A/B/C have different offsets.
    Module layouts = Program([new StringValue("a"), new StringValue("b"), new StringValue("c"), new StringValue("live")], 8,
    [
        new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
        new(OpCode.Null), new(OpCode.LoadCell, 0), new(OpCode.MakeScope, 2, Text: "other\0item"), new(OpCode.StoreLocal, 1),
        new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.StoreLocal, 2),
        new(OpCode.Constant, 3), new(OpCode.StoreLocal, 0),
        new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.StoreLocal, 3),
        new(OpCode.Constant, 1), new(OpCode.Null), new(OpCode.MakeScope, 2, Text: "item\0other"), new(OpCode.StoreLocal, 1),
        new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.StoreLocal, 4),
        new(OpCode.Null), new(OpCode.Null), new(OpCode.Constant, 2), new(OpCode.MakeScope, 3, Text: "third\0other\0item"), new(OpCode.StoreLocal, 1),
        new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.StoreLocal, 5),
        new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 5),
        new(OpCode.MakeScope, 4, Text: "a\0live\0b\0c"), new(OpCode.Return)
    ], new FunctionCode("read-item", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "item"), new(OpCode.Return)],
        TypeRef.String, ParameterTypes: [TypeRef.Scope]));
    foreach (Module module in new[] { layouts, Codec(layouts) })
    {
        ScopeValue result = (ScopeValue)await Run(module);
        Check(Text(result.Members["a"]) == "a" && Text(result.Members["live"]) == "live"
            && Text(result.Members["b"]) == "b" && Text(result.Members["c"]) == "c", "Field cache confused a payload or layout with a stable location");
    }

    SourceSpan missingSpan = new("missing-member-review", 54, 4);
    Module missing = layouts with
    {
        Functions =
    [
        layouts.Functions[0] with { Instructions =
        [
            .. layouts.Functions[0].Instructions.Take(16),
            new(OpCode.Null), new(OpCode.MakeScope, 1, Text: "other"), new(OpCode.StoreLocal, 1),
            new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.Return)
        ] },
        layouts.Functions[1] with { Instructions =
            [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "item", Span: missingSpan), new(OpCode.Return)] }
    ]
    };
    bool missingFailed = false;
    try { await Run(missing); }
    catch (MsyncException error) { missingFailed = error.Span == missingSpan; }
    Check(missingFailed, "A warmed field site lost the selected missing-field error/span");

    foreach ((Value left, Value right) in new (Value, Value)[]
    {
        (new NumberValue(-0.0), new NumberValue(-0.0)),
        (new NumberValue(double.PositiveInfinity), new NumberValue(double.NegativeInfinity)),
        (new StringValue("left"), new StringValue("right"))
    })
    {
        Module arithmetic = Program([left, right], 0,
            [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Call, 2, 2), new(OpCode.Return)],
            new FunctionCode("add", 2, 2, [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.Return)], TypeRef.Any,
                ParameterTypes: [TypeRef.Union(TypeRef.Number, TypeRef.String), TypeRef.Union(TypeRef.Number, TypeRef.String)]));
        Value result = await Run(arithmetic);
        Check((left, right, result) switch
        {
            (NumberValue a, NumberValue b, NumberValue actual) => BitConverter.DoubleToInt64Bits(actual.Data) == BitConverter.DoubleToInt64Bits(a.Data + b.Data),
            (StringValue a, StringValue b, StringValue actual) => actual.Data == a.Data + b.Data,
            _ => false
        }, "A numeric fusion changed IEEE representation or nonnumeric fallback");
    }
    SourceSpan addSpan = new("numeric-fallback-review", 21, 1);
    Module invalidAdd = Program([new StringValue("text"), new NumberValue(1)], 0,
        [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Call, 2, 2), new(OpCode.Return)],
        new FunctionCode("add", 2, 2, [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add, Span: addSpan), new(OpCode.Return)], TypeRef.Any,
            ParameterTypes: [TypeRef.Union(TypeRef.Number, TypeRef.String), TypeRef.Union(TypeRef.Number, TypeRef.String)]));
    bool addFailed = false;
    try { await Run(invalidAdd); }
    catch (InvalidCastException) { addFailed = true; }
    Check(addFailed, "A fused type miss changed the existing raw Add failure");
    Console.WriteLine($"PASS: {assertions} independent dispatch/capture/layout/numeric ABI contracts");
}
finally { Directory.Delete(root, recursive: true); }
