using System.Globalization;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
bool validateOnly = args.Contains("--validate", StringComparer.Ordinal);
string[] requested = args.Where(argument => argument != "--validate").ToArray();
IEnumerable<string> paths = requested.Length == 0
    ? Directory.EnumerateFiles(fixtures).Where(path => Path.GetExtension(path) is ".mso" or ".mexe").Order()
    : requested;
int checks = 0;
foreach (string path in paths)
{
    LegacyV6Module legacy = LegacyV6Reader.Read(path);
    NativeProgram program = NativeLegacyV6Importer.Import(legacy);
    try { NativeArtifact.Validate(program); }
    catch
    {
        Directory.CreateDirectory(".temp/legacy-v6-import");
        File.WriteAllText(".temp/legacy-v6-import/failed-native.txt", string.Join('\n', program.Functions.SelectMany(f =>
            f.Instructions.Select((instruction, pc) => $"{f.Name}:{pc}: {instruction} RT={(instruction.Result < 0 ? -1 : f.Slots[instruction.Result].Type)} AT={(instruction.A < 0 || instruction.A >= f.Slots.Count ? -1 : f.Slots[instruction.A].Type)}"))));
        File.WriteAllText(".temp/legacy-v6-import/failed-types.txt", string.Join('\n', program.Types.Select((type, index) => $"{index}: {type} layouts={string.Join(',', type.Layouts ?? [])}")));
        throw;
    }
    using MemoryStream artifact = new();
    NativeArtifact.Write(program, artifact);
    artifact.Position = 0;
    NativeProgram detached = NativeArtifact.Read(artifact);
    if (validateOnly) { Console.WriteLine($"PASS {path}"); checks++; continue; }
    string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    string input = Path.Combine(directory, "stdin.utf8");
    string expected = Path.ChangeExtension(path, ".expected");
    if (!File.Exists(expected)) expected = Path.Combine(directory, "expected.utf8");
    StringWriter output = new(CultureInfo.InvariantCulture);
    StringWriter error = new(CultureInfo.InvariantCulture);
    using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(3));
    await using NativeInterpreter interpreter = new(detached, new(directory, path, Output: output, Error: error)
    { Input = new StringReader(File.Exists(input) ? File.ReadAllText(input) : "") });
    await interpreter.ExecuteAsync(cancellation.Token);
    string actual = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    if (File.Exists(expected) && actual != File.ReadAllText(expected).Replace("\r\n", "\n", StringComparison.Ordinal))
        throw new Exception($"{path}: expected {File.ReadAllText(expected)}, received {actual}");
    if (error.ToString().Length != 0 || interpreter.FailedTransfers != 0)
        throw new Exception($"{path}: {error}; failed transfers={interpreter.FailedTransfers}");
    Console.WriteLine($"PASS {Path.GetFileName(path)}");
    checks++;
}
if (!validateOnly) { await ConstantPoolIdentity(); await PlainFunctionIdentity(); await AllocatorReflection(); await HistoricalBindings(); checks += 4; }
if (checks == 0) throw new Exception("No format-6 fixtures were exercised.");
Console.WriteLine($"{checks} detached format-6 native-import journeys passed.");

async Task ConstantPoolIdentity()
{
    LegacyV6Module legacy = new(
        [new((ushort)TypeKind.Null, -1, []), new((ushort)TypeKind.Number, -1, []), new((ushort)TypeKind.Scope, -1, [0])],
        [new([new("n", 1)])], [], [new("n", [new(0, 0)])],
        [new(LegacyV6ValueKind.Namespace, Layout: 0, Items: [new(LegacyV6ValueKind.Number, Number: 1)]), new(LegacyV6ValueKind.Number, Number: 42)],
        [new("constant-pool-identity", 0, new([], 1, [], [], false),
            [new(LegacyV6Op.Constant, 0, 0), new(LegacyV6Op.Constant, 1, 0), new(LegacyV6Op.StoreField, 0, 0),
             new(LegacyV6Op.Pop, 0, 0), new(LegacyV6Op.Constant, 0, 0), new(LegacyV6Op.LoadField, 0, 0), new(LegacyV6Op.Return, 0, 0)])],
        [], 0, ArtifactStage.Relocatable, null, 2, ModuleSemantics.Current);
    NativeProgram program = NativeLegacyV6Importer.Import(legacy);
    using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes), new(Environment.CurrentDirectory, "constant-pool-identity"));
    NativeValue actual = await interpreter.ExecuteAsync();
    if (actual.Kind != NativeKind.Number || actual.AsNumber() != 42) throw new Exception("A format-6 constant pool lost namespace allocation identity.");
    Console.WriteLine("PASS constant-pool-identity");
}

async Task PlainFunctionIdentity()
{
    LegacyV6Module legacy = new(
        [new((ushort)TypeKind.Null, -1, []), new((ushort)TypeKind.Function, -1, []), new((ushort)TypeKind.Bool, -1, [])],
        [], [], [], [new(LegacyV6ValueKind.Function, Function: 1), new(LegacyV6ValueKind.Function, Function: 1)],
        [new("plain-function-identity", 0, new([], 2, [], [], false),
            [new(LegacyV6Op.Constant, 0, 0), new(LegacyV6Op.MakeRange, 1, 0), new(LegacyV6Op.Constant, 1, 0),
             new(LegacyV6Op.NativeCall, 2, 0, "ranges.contains"), new(LegacyV6Op.Return, 0, 0)]),
         new("plain", 0, new([], 0, [], [], false), [new(LegacyV6Op.Null, 0, 0), new(LegacyV6Op.Return, 0, 0)])],
        [], 0, ArtifactStage.Relocatable, null, 2, ModuleSemantics.Current);
    NativeProgram program = NativeLegacyV6Importer.Import(legacy);
    using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes), new(Environment.CurrentDirectory, "plain-function-identity"));
    NativeValue actual = await interpreter.ExecuteAsync();
    if (actual.Kind != NativeKind.Bool || !actual.AsBool()) throw new Exception("Distinct format-6 plain function constants changed reference equality.");
    Console.WriteLine("PASS plain-function-identity");
}

async Task AllocatorReflection()
{
    LegacyV6Module legacy = new(
        [new((ushort)TypeKind.Null, -1, []), new((ushort)TypeKind.String, -1, [])], [], [], [],
        [new(LegacyV6ValueKind.Function, NativeName: "storage.vector_push")],
        [new("allocator-reflection", 0, new([], 1, [], [], false),
            [new(LegacyV6Op.Constant, 0, 0), new(LegacyV6Op.NativeCall, 1, 0, "meta.parameter_types"),
             new(LegacyV6Op.NativeCall, 1, 0, "ranges.front"), new(LegacyV6Op.NativeCall, 1, 0, "meta.type_name"),
             new(LegacyV6Op.Return, 0, 0)])], [], 0, ArtifactStage.Relocatable, null, 2, ModuleSemantics.Current);
    NativeProgram program = NativeLegacyV6Importer.Import(legacy);
    using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes), new(Environment.CurrentDirectory, "allocator-reflection"));
    NativeValue actual = await interpreter.ExecuteAsync();
    if (actual.Kind != NativeKind.String || actual.AsString() != "Storage") throw new Exception("A retired native adapter exposed its physical header ABI through callable reflection.");
    Console.WriteLine("PASS allocator-reflection");
}

async Task HistoricalBindings()
{
    NativeHistoricalBindingBundle bundle = NativeHistoricalBindings.Lower(
        ["storage.vector_new", "storage.vector_push", "storage.vector_get", "arena.new", "arena.allocate", "arena.key",
         "slots.new", "slots.allocate", "slots.get"], [TypeRef.Number, TypeRef.Scope, new(TypeKind.Storage, TypeRef.Number)]);
    using MemoryStream bytes = new(); NativeArtifact.Write(bundle.Program, bytes); bytes.Position = 0;
    NativeProgram program = NativeArtifact.Read(bytes);
    int payloadLayout = program.Layouts.Count;
    int numberType = program.Types.Select((type, index) => (type, index)).First(item => item.type.Kind == TypeKind.Number && item.type.Pointee < 0).index;
    program = program with { Layouts = [.. program.Layouts, new([new("n", numberType)])] };
    NativeArtifact.Validate(program);
    if (program.Types.Any(type => (type.Kind & TypeKind.Storage) != 0)
        || program.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == NativeOp.CallNative
            && instruction.Symbol is { } symbol && (symbol.StartsWith("storage.", StringComparison.Ordinal)
                || symbol.StartsWith("arena.", StringComparison.Ordinal) || symbol.StartsWith("slots.", StringComparison.Ordinal))))
        throw new Exception("Historical binding generation retained a runtime owner kind or capability.");
    await using NativeInterpreter interpreter = new(program);
    ValueTask<NativeValue> Call(string name, params NativeValue[] arguments) => interpreter.InvokeAsync(
        NativeValue.FromFunction(new(bundle.Functions[name])), arguments, CancellationToken.None);
    NativeValue owner = await Call("storage.vector_new", NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(3)], TypeRef.Number)));
    if ((await Call("storage.vector_push", owner, NativeValue.FromNumber(7))).AsNumber() != 1
        || (await Call("storage.vector_get", owner, NativeValue.FromNumber(1))).AsNumber() != 7)
        throw new Exception("Historical native adapters lost vector storage behavior.");
    NativeValue description = await interpreter.InvokeAsync(NativeValue.FromFunction(new(bundle.LogicalTypeFunction)), [owner], CancellationToken.None);
    if (description.AsType() != new TypeRef(TypeKind.Storage, TypeRef.Number))
        throw new Exception("Historical native adapter lost its logical type descriptor.");
    NativeRange nestedSource = NativeRange.Snapshot([owner], new(TypeKind.Storage, TypeRef.Number), NativeType.Of(NativeKind.Address));
    NativeValue nested = await Call("storage.vector_new", NativeValue.FromRange(nestedSource));
    if ((await Call("storage.vector_get", nested, NativeValue.FromNumber(0))).AsAddress() != owner.AsAddress())
        throw new Exception("Historical nested descriptors changed the physical namespace reference ABI.");
    foreach (NativeValue value in new[] { owner, NativeValue.FromNumber(8), NativeValue.FromType(TypeRef.Number) })
    {
        NativeValue expected = NativeValue.FromType(value == owner ? description.AsType() : TypeRef.Number);
        if (!(await interpreter.InvokeAsync(NativeValue.FromFunction(new(bundle.TypeTestFunction)), [value, expected], CancellationToken.None)).AsBool())
            throw new Exception("Historical type testing changed header or Type-valued denotation semantics.");
    }
    NativeAllocation payload = new(new NativeLayout([new NativeField("n", NativeType.Of(NativeKind.Number))]), payloadLayout);
    payload.Field(0).Store(NativeValue.FromNumber(42));
    NativeValue item = NativeValue.FromAddress(payload.Address);
    NativeValue arena = await Call("arena.new", NativeValue.FromType(TypeRef.Scope));
    NativeValue index = await Call("arena.allocate", arena, item, NativeValue.FromNumber(0));
    if ((await Call("arena.key", arena, index)).AsAddress() != item.AsAddress())
        throw new Exception("Historical arena adapters copied a namespace instead of its reference.");
    NativeValue slots = await Call("slots.new", NativeValue.FromType(TypeRef.Scope));
    index = await Call("slots.allocate", slots, item);
    if ((await Call("slots.get", slots, index)).AsAddress() != item.AsAddress())
        throw new Exception("Historical slot adapters copied a namespace instead of its reference.");
    Console.WriteLine("PASS historical-native-binding-bundle");
}
