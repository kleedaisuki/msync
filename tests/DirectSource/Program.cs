using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

const long Payload = unchecked((long)0xfff8000000012345UL);
string root = Environment.CurrentDirectory;
bool nativeEnabled = !string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase);
bool? native = !nativeEnabled ? false : args.Contains("--expect-native", StringComparer.Ordinal) ? true
    : RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
        Architecture.Arm64 when OperatingSystem.IsLinux() => null,
        _ => false,
    };

foreach (bool implicitExit in new[] { false, true })
{
    Module portable = RoundTrip(Fixture(implicitExit));
    foreach (Module image in new[] { portable, RoundTrip(Binding.Bind(portable, new(root, root))) })
    {
        if (!implicitExit)
            CheckResult(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root)));
        foreach (bool detailed in new[] { false, true })
        {
            RuntimeProfile profile = new(instructionSites: detailed);
            CheckResult(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile }));
            var samples = profile.Drain();
            ulong compiledCell = CheckUpdate("update cell", 73, 2);
            ulong compiledValue = CheckUpdate("update value", 72, 0);
            ulong CheckUpdate(string name, ulong calls, ulong nonPositive)
            {
                var update = samples.Single(sample => sample.Name == "vm.function" && sample.Attributes["function.name"] == name);
                ulong direct = update.Metrics["direct_source_calls"];
                ulong perCall = implicitExit && image.Stage == ArtifactStage.Executable ? 10UL : 11UL;
                ulong? expectedDirect = detailed || implicitExit || native == false ? 0UL : native == true ? 9UL : null;
                if ((expectedDirect is { } count ? direct != count : direct is not (0 or 9))
                    || update.Metrics["calls"] + direct != calls || update.Metrics["jit_entries"] != direct
                    || update.Metrics["instructions"] != calls * perCall - nonPositive)
                    throw new InvalidOperationException(name + ": " + string.Join(", ", update.Metrics));
                return direct;
            }
            ulong writes = samples.Single(sample => sample.Name == "vm.function" && sample.Attributes["function.name"] == "main")
                .Metrics["raw_local_writes"];
            if (writes != (compiledCell != 0 ? 71UL : 70UL))
                throw new InvalidOperationException($"Unexpected raw result transport: {writes} raw local writes.");
            Console.WriteLine($"PASS {image.Stage}: implicit={implicitExit}, detailed={detailed}, cell native={compiledCell}, value native={compiledValue}");
        }
    }
}

static void CheckResult(Value value)
{
    var result = (ScopeValue)value;
    EqualBits(result["zero"], long.MinValue);
    EqualBits(result["nan"], Payload);
    foreach (string field in new[] { "fallback", "before", "after" }) EqualBits(result[field], BitConverter.DoubleToInt64Bits(115));
    foreach (string field in new[] { "plain1", "plain2" }) EqualBits(result[field], BitConverter.DoubleToInt64Bits(43));
}

static Module Fixture(bool implicitExit)
{
    const int zero = 0, one = 1, seed = 2, nan = 3, seventy = 4;
    const int shared = 0, target = 1, iteration = 2, plain = 3, signedZero = 4;
    const int number = 0, result = 1, fields = 0;
    List<Instruction> main =
    [
        new(OpCode.Constant, seed), new(OpCode.InitializeLocal, shared),
        new(OpCode.LoadCell, shared, number + 1), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, target),
        new(OpCode.Constant, seed), new(OpCode.MakeClosure, 2, 1), new(OpCode.InitializeLocal, plain),
        new(OpCode.Constant, zero), new(OpCode.InitializeLocal, iteration),
    ];
    int header = main.Count;
    main.AddRange([new(OpCode.LoadLocal, iteration), new(OpCode.Constant, seventy), new(OpCode.Less)]);
    int conditional = main.Count;
    main.AddRange([
        new(OpCode.JumpIfFalse), new(OpCode.LoadLocal, target), new(OpCode.Constant, one), new(OpCode.Call, 1, 2), new(OpCode.Pop),
        new(OpCode.LoadLocal, plain), new(OpCode.Constant, one), new(OpCode.Call, 1, 3), new(OpCode.Pop),
        new(OpCode.LoadLocal, iteration), new(OpCode.Constant, one), new(OpCode.Add), new(OpCode.StoreLocal, iteration),
        new(OpCode.Jump, header),
    ]);
    main[conditional] = main[conditional] with { A = main.Count };
    main.AddRange([
        new(OpCode.LoadLocal, target), new(OpCode.Constant, zero), new(OpCode.Negate), new(OpCode.Call, 1, 2),
        new(OpCode.InitializeLocal, signedZero), new(OpCode.LoadStorage, signedZero),
        new(OpCode.LoadLocal, target), new(OpCode.Constant, nan), new(OpCode.Call, 1, 2),
        // An indirect call keeps its ordinary frame even after the target's
        // direct sites have warmed. Portable arguments are values, not cells.
        new(OpCode.LoadLocal, target), new(OpCode.Constant, one), new(OpCode.Call, 1),
        new(OpCode.LoadLocal, shared),
        // The value-capture ABI uses the same body, but rebinding affects only
        // the activation, never the shared location of the other function.
        new(OpCode.LoadLocal, plain), new(OpCode.Constant, one), new(OpCode.Call, 1, 3),
        new(OpCode.LoadLocal, plain), new(OpCode.Constant, one), new(OpCode.Call, 1, 3),
        new(OpCode.LoadLocal, shared),
        new(OpCode.NewScope, fields), new(OpCode.Return),
    ]);

    FunctionCode Update(string name, bool location) => new(name, 1, 2, [
        new(OpCode.LoadLocal, 0), new(OpCode.Constant, one), new(OpCode.Add), new(OpCode.StoreLocal, 0),
        new(OpCode.LoadLocal, 1), new(OpCode.Constant, zero), new(OpCode.Greater), new(OpCode.JumpIfFalse, 10),
        new(OpCode.LoadLocal, 0), new(OpCode.Jump, 11), new(OpCode.LoadLocal, 1),
        new(implicitExit ? OpCode.Nop : OpCode.Return),
    ], TypeRef.Number, CaptureCount: 1, ParameterTypes: [TypeRef.Number])
    { CodeSignature = new([number], number, [new(number, location)]) };

    return new Module(
        [new NumberValue(0), new NumberValue(1), new NumberValue(42),
         new NumberValue(BitConverter.Int64BitsToDouble(Payload)), new NumberValue(70)],
        [
            new("main", 0, 5, main.ToArray(), TypeRef.Scope) { CodeSignature = new([], result, []) },
            Update("update cell", location: true), Update("update value", location: false),
        ], OptimizationLevel: 0)
    {
        CodeTypes = [new(TypeKind.Number), new(TypeKind.Scope, ScopeLayouts: [fields])],
        ScopeLayouts = [new(new[] { "zero", "nan", "fallback", "before", "plain1", "plain2", "after" }
            .Select(name => new CodeField(name, number)).ToArray())]
    };
}

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes, formBlocks: false);
}

static void EqualBits(Value actual, long expected)
{
    if (actual is not NumberValue number || BitConverter.DoubleToInt64Bits(number.Data) != expected)
        throw new InvalidOperationException($"Wrong Number bits for {actual}, expected {expected:x16}.");
}
