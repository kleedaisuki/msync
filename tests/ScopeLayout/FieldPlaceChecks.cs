using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class FieldPlaceChecks
{
    private const int Number = 0, Null = 1, FirstOrThird = 2, Result = 3, Opaque = 4;
    private const int CommonX = 0, DistinctX = 1, FirstX = 2, DynamicX = 3;

    private static readonly CodeType[] Types =
    [
        new(TypeKind.Number), new(TypeKind.Null), new(TypeKind.Scope, ScopeLayouts: [0, 2]),
        new(TypeKind.Scope, ScopeLayouts: [3]), new(TypeKind.Scope)
    ];

    private static readonly CodeLayout[] Layouts =
    [
        new([new("x", Number), new("left", Null)]),
        new([new("x", Number), new("right", Null)]),
        new([new("right", Null), new("x", Number)]),
        new([new("row", Opaque), new("old", Number)])
    ];

    private static readonly CodeFieldProjection[] Places =
    [
        new("x", [new(0, 0), new(1, 0)]), new("x", [new(0, 0), new(2, 1)]),
        new("x", [new(0, 0)]), new("x", [])
    ];

    private static readonly Value[] Rows =
    [
        Row(0, ["x", "left"], 10, 0), Row(1, ["x", "right"], 20, 0),
        Row(2, ["right", "x"], 30, 1), new NumberValue(41)
    ];

    internal static async Task Run()
    {
        var common = Image([
            .. Choose(0, 1),
            new(OpCode.Dup), new(OpCode.LoadField, CommonX), new(OpCode.InitializeLocal, 0),
            new(OpCode.Dup), new(OpCode.Constant, 3), new(OpCode.StoreField, CommonX), new(OpCode.Pop),
            new(OpCode.LoadLocal, 0), new(OpCode.NewScope, 3), new(OpCode.Return)
        ], Result);
        foreach (var (input, expected) in new[] { ("line\n", 10d), ("", 20d) })
        {
            var bound = RoundTrip(common);
            Require(bound.FieldProjections[CommonX].Cases.SequenceEqual([new(0, 0), new(1, 0)]),
                "Portable bytecode did not retain both layouts with the common physical ordinal.");
            var value = (ScopeValue)await Execute(bound, input);
            var row = (ScopeValue)value["row"];
            Require(value["old"] is NumberValue { Data: var old } && old == expected
                && row["x"] is NumberValue { Data: 41 }, "The typed place loaded or stored the wrong row.");
        }
        RuntimeProfile profile = new(instructionSites: true);
        _ = await Execute(RoundTrip(common), profile: profile);
        var sites = profile.Drain().Where(sample => sample.Name == "vm.instruction").ToArray();
        Require(Saw(sites, "7", "LoadField") && Saw(sites, "11", "StoreField"),
            "Typed operations must retain original portable PCs and opcode counts.");

        var differing = Image([.. Choose(0, 2), new(OpCode.LoadField, DistinctX), new(OpCode.Return)]);
        foreach (var (input, expected) in new[] { ("line\n", 10d), ("", 30d) })
            Require(await Execute(RoundTrip(differing), input) is NumberValue { Data: var actual } && actual == expected,
                "A multi-layout projection must use the selected layout's own ordinal.");
        Reject(Replace(differing, 6, new(OpCode.LoadField, FirstX)), "no case for receiver layout");

        var loop = Image([
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, DistinctX),
            new(OpCode.InitializeLocal, 1), new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull),
            new(OpCode.JumpIfTrue, 11), new(OpCode.Constant, 2), new(OpCode.StoreLocal, 0), new(OpCode.Jump, 2),
            new(OpCode.LoadLocal, 1), new(OpCode.Return)
        ]);
        Require(await Execute(RoundTrip(loop)) is NumberValue { Data: 10 }
            && await Execute(RoundTrip(loop), "line\n") is NumberValue { Data: 30 },
            "Backedge layout changes must remain visible through the typed projection.");
        Reject(Replace(loop, 3, new(OpCode.LoadField, FirstX)), "no case for receiver layout");

        FunctionCode opaque = new("opaque receiver", 1, 1,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, DynamicX), new(OpCode.Return)], TypeRef.Number,
            ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([Opaque], Number, []) };
        var dynamic = Image([new(OpCode.MakeClosure, 1), new(OpCode.Constant, 2), new(OpCode.Call, 1, 2), new(OpCode.Return)]);
        dynamic = dynamic with { Functions = [dynamic.Functions[0], opaque] };
        Require(await Execute(RoundTrip(dynamic)) is NumberValue { Data: 30 },
            "An opaque Object receiver must read its field without assuming a physical ordinal.");

        var captured = Image([
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadCell, 0, FirstOrThird + 1), new(OpCode.InitializeLocal, 1),
            new(OpCode.Constant, 2), new(OpCode.StoreLocal, 1), new(OpCode.LoadLocal, 0),
            new(OpCode.LoadField, DistinctX), new(OpCode.Return)
        ]);
        Require(await Execute(RoundTrip(captured)) is NumberValue { Data: 30 },
            "A shared location must observe its later layout-changing assignment.");
        Reject(Replace(captured, 7, new(OpCode.LoadField, FirstX)), "no case for receiver layout");

        var copied = Image([
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.LoadCell, 0, FirstOrThird + 1), new(OpCode.InitializeLocal, 2),
            new(OpCode.Constant, 2), new(OpCode.StoreLocal, 2), new(OpCode.LoadLocal, 1),
            new(OpCode.LoadField, FirstX), new(OpCode.Return)
        ]);
        Require(await Execute(RoundTrip(copied)) is NumberValue { Data: 10 },
            "Rebinding a shared location must not change a copied receiver.");

        await NamedSource();
        Console.WriteLine("PASS source-free typed field places, layout joins, Cell aliases and named initialization");
    }

    private static async Task NamedSource()
    {
        const string source = """
            let initialized = 0;
            fn seed(n: Number) { global initialized; initialized = initialized + 1; n };
            fn get(n: Number) { object Named { x: seed(n) }; Named };
            let before = initialized;
            let first = get(13);
            first::x = 41;
            let second = get(22);
            object { before: before, count: initialized, value: second::x }
            """;
        var portable = Compiler.Compile(source, "named-field-place", 0);
        foreach (var image in new[] { portable, RoundTrip(portable), RoundTrip(Binding.Bind(portable, Context)) })
        {
            var result = (ScopeValue)await Execute(image);
            Require(result["before"] is NumberValue { Data: 0 }
                && result["count"] is NumberValue { Data: 1 }
                && result["value"] is NumberValue { Data: 41 },
                "Named initialization ran early, ran twice or lost its winning alias.");
        }
    }

    private static ScopeValue Row(int layout, string[] names, double value, int offset) =>
        new(new ScopeLayout(names, layout),
            offset == 0 ? [new NumberValue(value), Value.Null] : [Value.Null, new NumberValue(value)]);

    private static Instruction[] Choose(int first, int second) =>
    [
        new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 5),
        new(OpCode.Constant, first), new(OpCode.Jump, 6), new(OpCode.Constant, second)
    ];

    private static BindingContext Context => new(Environment.CurrentDirectory, Environment.CurrentDirectory);

    private static Module Image(Instruction[] instructions, int result = Number) =>
        new(Rows, [new FunctionCode("field places", 0, 3, instructions, result == Number ? TypeRef.Number : TypeRef.Scope)
        { CodeSignature = new([], result, []) }], Stage: ArtifactStage.Executable, Context: Context)
        { CodeTypes = Types, ScopeLayouts = Layouts, FieldProjections = Places };

    private static Module Replace(Module image, int pc, Instruction replacement)
    {
        var code = image.Functions[0].Instructions.ToArray();
        code[pc] = replacement;
        return image with { Functions = [image.Functions[0] with { Instructions = code }, .. image.Functions.Skip(1)] };
    }

    private static void Reject(Module module, string diagnostic)
    {
        using MemoryStream stream = new();
        Artifact.WriteProduced(module, stream);
        stream.Position = 0;
        try { _ = Artifact.Read(stream, formBlocks: false); }
        catch (MsyncException error) when (error.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("The reader accepted an incomplete field projection.");
    }

    private static bool Saw(RuntimeProfileSample[] sites, string pc, string opcode) =>
        sites.Any(sample => sample.Attributes["instruction.pc"] == pc
            && sample.Attributes["opcode"] == opcode && sample.Metrics["hits"] == 1);

    private static ValueTask<Value> Execute(Module image, string input = "", RuntimeProfile? profile = null) =>
        Interpreter.ExecuteAsync(image, new() { Input = new StringReader(input), Profile = profile });

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
