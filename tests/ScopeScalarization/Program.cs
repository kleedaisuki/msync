using Msync.Optimization;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

const int Number = 0, Text = 1, Null = 2, Opaque = 3, NumericRow = 4, StringRow = 5, ValueRow = 6;
const int NumericLayout = 0, StringLayout = 1, ValueLayout = 2, NestedLayout = 3, EmptyLayout = 4;
const int ItemField = 0, ValueField = 1, MissingField = 2;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

MethodInfo scalarize = typeof(Optimizer).Assembly.GetType("Msync.Optimization.ScopeScalarization")!
    .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
Module Scalarize(Module module) => module with
{
    Functions = module.Functions.Select(function => (FunctionCode)scalarize.Invoke(null, [module, function])!).ToArray(),
    Blocks = null
};

string Snapshot(Value value) => value switch
{
    ScopeValue scope => "{" + string.Join(',', scope.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + Snapshot(pair.Value))) + "}",
    _ => value.Type + ":" + value
};

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "scope-scalarization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    async Task<string> Execute(Module module)
    {
        try { return Snapshot(await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root))); }
        catch (MsyncException exception) { return "error:" + exception.Message + ":" + exception.Span; }
    }

    foreach ((string name, string source, bool eliminated) in new[]
    {
        ("write-read", "let value = exists ./flag; let box = scope { item: false }; box::item = value; box::item", true),
        ("alias-write", "let box = scope { item: 0 }; let alias = box; alias::item = 9; box::item", true),
        ("last-write", "let box = scope { item: 0 }; box::item = 4; box::item = 9; box::item", true),
        ("escaped-return", "let box = scope { item: 1 }; box::item = 9; box", false),
        ("escaped-read", "let box = scope { item: 1 }; box::item = 9; let seen = box::item; scope { object: box, seen: seen }", false),
        ("captured-object", "let box = scope { item: 0 }; let mutate = fn { box::item = 9; }; mutate(); box::item", false),
        ("captured-rebinding", "let box = scope { item: 1 }; let other = scope { item: 2 }; let replace = fn { box = other; }; replace(); box::item = 9; other::item", false),
        ("captured-field-store", "let Box = scope { item: 1, update: fn { item = 7; } }; Box::update(); Box::item", true),
        ("captured-field-read", "let Box = scope { item: 1, read: fn { item } }; Box::item = 9; Box::read()", true),
        ("branch-alias", "let box = scope { item: 0 }; if exists ./flag { box::item = 9; }; box::item", false),
        ("loop-liveout", "let previous = scope { item: 0 }; for item in [1, 2] { let box = scope { item: item }; previous = box; }; previous::item", false),
        ("rebound-alias", "let box = scope { item: 1 }; let alias = box; alias = scope { item: 2 }; alias::item = 9; box::item", true)
    })
    {
        Module original = Compiler.Compile(source, name, 0);
        Module transformed = Scalarize(original);
        if (eliminated)
        {
            Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.NewScope), name + ": private object still allocated");
            Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.LoadField or OpCode.StoreField)), name + ": private field operations remain");
        }
        Module twice = Scalarize(transformed);
        Require(twice.Functions.SelectMany(function => function.Instructions).SequenceEqual(transformed.Functions.SelectMany(function => function.Instructions)), name + ": scalarization is not idempotent");
        foreach (bool present in new[] { false, true })
        {
            string flag = Path.Combine(root, "flag");
            if (present) await File.WriteAllTextAsync(flag, "present");
            else File.Delete(flag);
            string expected = await Execute(original);
            Require(await Execute(transformed) == expected, name + ": scalarized result differs");
            Require(await Execute(Optimizer.Optimize(original, 3)) == expected, name + ": integrated optimizer result differs");
            foreach (Module candidate in new[] { transformed, Binding.Bind(transformed, new(root, root)) })
            {
                Artifact.Validate(candidate);
                using MemoryStream stream = new();
                Artifact.Write(candidate, stream); stream.Position = 0;
                Require(await Execute(Artifact.Read(stream)) == expected, name + ": serialized result differs");
            }
        }
    }
    Module cellAliases = Image([new NumberValue(1), new NumberValue(2)], [new("cells", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.NewScope, NumericLayout), new(OpCode.InitializeLocal, 0),
         new(OpCode.LoadCell, 0, NumericRow + 1), new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 1), new(OpCode.NewScope, NumericLayout),
         new(OpCode.StoreLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, ItemField), new(OpCode.Return)], TypeRef.Number)
         { CodeSignature = new([], Number, []) }]);
    await CheckImages(cellAliases, Scalarize(cellAliases), "Number:2", "raw capture-cell alias store");
    Module capturedAcrossBlocks = Image([new StringValue("before"), new StringValue("replacement")],
        [new("cross-block-capture", 0, 2,
            [new(OpCode.Constant, 0), new(OpCode.NewScope, StringLayout), new(OpCode.InitializeLocal, 0),
             new(OpCode.LoadCell, 0, StringRow + 1), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, 1), new(OpCode.Jump, 7),
             new(OpCode.Constant, 1), new(OpCode.NewScope, StringLayout), new(OpCode.StoreLocal, 0),
             new(OpCode.LoadLocal, 1), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.String)
             { CodeSignature = new([], Text, []) },
         new("read-captured-scope", 0, 1,
            [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, StringRow), new(OpCode.LoadField, ItemField), new(OpCode.Return)],
            TypeRef.String, CaptureCount: 1) { CodeSignature = new([], Text, [new(StringRow, true)]) }]);
    await CheckImages(capturedAcrossBlocks, Scalarize(capturedAcrossBlocks), "String:replacement", "cross-block captured rebinding");
    var freshCode = capturedAcrossBlocks.Functions[0].Instructions.ToArray();
    freshCode[9] = new(OpCode.InitializeLocal, 0);
    freshCode[10] = new(OpCode.LoadLocal, 0);
    freshCode[11] = new(OpCode.LoadField, ItemField);
    Module freshIdentity = capturedAcrossBlocks with { Functions = [capturedAcrossBlocks.Functions[0] with { Instructions = freshCode }, capturedAcrossBlocks.Functions[1]] };
    Module freshScalarized = Scalarize(freshIdentity);
    Require(freshScalarized.Functions[0].Instructions.Count(instruction => instruction.Op == OpCode.NewScope) == 1,
        "a fresh lexical identity inherited a previous cell's escape state");
    await CheckImages(freshIdentity, freshScalarized, "String:replacement", "fresh lexical identity");
    Module fieldAliases = Image([new NumberValue(1), new NumberValue(2)], [new("fields", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, Number + 1), new(OpCode.Pop),
         new(OpCode.LoadStorage, 0), new(OpCode.NewScope, NumericLayout), new(OpCode.InitializeLocal, 1),
         new(OpCode.LoadLocal, 1), new(OpCode.Constant, 1), new(OpCode.StoreField, ItemField), new(OpCode.Pop),
         new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number) { CodeSignature = new([], Number, []) }]);
    await CheckImages(fieldAliases, Scalarize(fieldAliases), "Number:2", "scalar field store shares its captured location");
    Module captureAliases = Image([new NumberValue(10), new NumberValue(20)],
        [new("capture-alias", 0, 4,
            [new(OpCode.Constant, 0), new(OpCode.NewScope, ValueLayout), new(OpCode.InitializeLocal, 0),
             new(OpCode.LoadCell, 0, ValueRow + 1), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, 1),
             new(OpCode.LoadLocal, 1), new(OpCode.GetCapture, 0), new(OpCode.InitializeLocal, 2),
             new(OpCode.LoadStorage, 2), new(OpCode.NewScope, NestedLayout), new(OpCode.InitializeLocal, 3),
             new(OpCode.LoadLocal, 3), new(OpCode.Constant, 1), new(OpCode.NewScope, ValueLayout),
             new(OpCode.StoreField, ItemField), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, ValueField), new(OpCode.Return)], TypeRef.Number)
             { CodeSignature = new([], Number, []) },
         new("captured", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, ValueRow), new(OpCode.Return)],
            TypeRef.Scope, CaptureCount: 1) { CodeSignature = new([], ValueRow, [new(ValueRow, true)]) }]);
    await CheckImages(captureAliases, Scalarize(captureAliases), "Number:20", "GetCapture field alias shares its original mutable location");
    SourceSpan missingSpan = new("opaque-scalarization", 4, 7);
    Module invalid = Image([], [
        new("missing", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.NewScope, EmptyLayout),
            new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Null) { CodeSignature = new([], Null, []) },
        new("opaque field", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, MissingField, Span: missingSpan),
            new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, ParameterTypes: [TypeRef.Scope])
            { CodeSignature = new([Opaque], Null, []) }
    ]);
    await CheckImages(invalid, Scalarize(invalid), $"error:Scope has no member 'missing':{missingSpan}", "selected opaque field diagnostic");
    Module fieldSlots = Compiler.Compile("let field = 0; if exists ./flag { field = 1; } else { field = 2; }; field", optimization: 0);
    var applicabilityScan = new ObservedInstructions(fieldSlots.Functions[0].Instructions);
    FunctionCode noAllocation = fieldSlots.Functions[0] with { Instructions = applicabilityScan };
    Module noWork = fieldSlots with { Functions = [noAllocation] };
    Require(ReferenceEquals(Scalarize(noWork).Functions[0], noAllocation), "Scope-free preparation changed the function");
    Require(applicabilityScan.Enumerations == 1 && applicabilityScan.IndexerReads == 0,
        "Scope-free preparation constructed CFG/liveness after its allocation applicability scan");
    Random random = new(80119);
    for (int sample = 0; sample < 24; sample++)
    {
        int[] values = [0, 1]; int alias = 0; int observed = 0;
        StringBuilder source = new("let left = scope { item: 0 }; let right = scope { item: 1 }; let alias = left; let observed = 0;\n");
        for (int step = 0; step < 64; step++)
        {
            if (random.Next(3) == 0)
            {
                alias = random.Next(2); source.Append("alias = ").Append(alias == 0 ? "left" : "right").AppendLine(";");
            }
            else
            {
                int selected = random.Next(3), value = random.Next(100);
                string target = selected switch { 0 => "left", 1 => "right", _ => "alias" };
                values[selected == 2 ? alias : selected] = value;
                source.Append(target).Append("::item = ").Append(value).AppendLine(";");
            }
            observed += values[alias]; source.AppendLine("observed = observed + alias::item;");
        }
        source.Append("observed + left::item + right::item");
        Module transformed = Scalarize(Compiler.Compile(source.ToString(), "alias-property", 0));
        Require(await Execute(transformed) == "Number:" + (observed + values[0] + values[1]), "generated alias memory versions differ from independent scalar model");
        Require(await Execute(Optimizer.Optimize(transformed, 3)) == "Number:" + (observed + values[0] + values[1]), "integrated alias memory versions differ from independent scalar model");
        Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.NewScope or OpCode.LoadField or OpCode.StoreField)), "generated private objects were not fully scalarized");
    }
    if (args.Contains("--performance", StringComparer.Ordinal))
    {
        foreach (int count in new[] { 100, 500, 2000 })
        {
            StringBuilder source = new("let dynamic = exists ./flag;\n");
            for (int index = 0; index < count; index++)
                source.Append($"let box{index} = scope {{ item: false }}; box{index}::item = dynamic; let read{index} = box{index}::item;\n");
            Module input = Compiler.Compile(source.ToString(), "scope-scaling", 0);
            for (int warm = 0; warm < 3; warm++) Scalarize(input);
            var times = new List<double>(); var allocations = new List<long>();
            for (int sample = 0; sample < 9; sample++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                Module result = Scalarize(input);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                Require(result.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.NewScope), "scaling scope not scalarized");
            }
            times.Sort(); allocations.Sort();
            Console.WriteLine($"{count} instances: {times[4]:F3} ms, {allocations[4] / 1_000_000.0:F3} MB allocation");
        }
    }
    Console.WriteLine($"Scope scalarization: {assertions} contracts passed.");

    async Task CheckImages(Module before, Module after, string expected, string label)
    {
        foreach (var module in new[] { before, after, Binding.Bind(after, new(root, root)) })
        {
            Require(await Execute(module) == expected, label + ": live result differs");
            using MemoryStream stream = new();
            Artifact.Write(module, stream); stream.Position = 0;
            Require(await Execute(Artifact.Read(stream)) == expected, label + ": serialized result differs");
        }
    }
}
finally { Directory.Delete(root, recursive: true); }

static Module Image(IReadOnlyList<Value> constants, IReadOnlyList<FunctionCode> functions) =>
    new(constants, functions, OptimizationLevel: 0)
    {
        CodeTypes = [
            new(TypeKind.Number), new(TypeKind.String), new(TypeKind.Null), new(TypeKind.Scope),
            new(TypeKind.Scope, ScopeLayouts: [NumericLayout]), new(TypeKind.Scope, ScopeLayouts: [StringLayout]),
            new(TypeKind.Scope, ScopeLayouts: [ValueLayout])
        ],
        ScopeLayouts = [
            new([new("item", Number)]), new([new("item", Text)]), new([new("value", Number)]),
            new([new("item", ValueRow)]), new([])
        ],
        FieldProjections = [
            new("item", [new(NumericLayout, 0), new(StringLayout, 0), new(NestedLayout, 0)]),
            new("value", [new(ValueLayout, 0)]), new("missing", [])
        ]
    };

internal sealed class ObservedInstructions(IReadOnlyList<Instruction> instructions) : IReadOnlyList<Instruction>
{
    public int Enumerations { get; private set; }
    public int IndexerReads { get; private set; }
    public int Count => instructions.Count;
    public Instruction this[int index] { get { IndexerReads++; return instructions[index]; } }
    public IEnumerator<Instruction> GetEnumerator()
    {
        Enumerations++;
        return instructions.GetEnumerator();
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
