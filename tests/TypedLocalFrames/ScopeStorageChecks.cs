using Msync.Core;
using Msync.Runtime;

internal static class ScopeStorageChecks
{
    private const int Zero = 0, NaN = 1, One = 2, Two = 3, SelectScope = 4, NewCellValue = 5;
    private const int ZeroSlot = 0, NaNSlot = 1, Counter = 2;
    private const int Any = 0, Number = 1, Bool = 2, OptionalText = 3, Scope = 4, OptionalScope = 5;
    private const int SnapshotRow = 0, SnapshotResult = 1, NaNZero = 2, BranchRow = 3, BranchResult = 4,
        ZeroNaN = 5, ZeroOnly = 6, SingletonResult = 7, LoopResult = 8;
    private static readonly CodeType[] Types = [new(TypeKind.Any), new(TypeKind.Number), new(TypeKind.Bool),
        new(TypeKind.String | TypeKind.Null), new(TypeKind.Scope), new(TypeKind.Scope | TypeKind.Null)];
    private static readonly CodeLayout[] Layouts =
    [
        new([new("zero", Number), new("nan", Number), new("shared", Number), new("line", OptionalText)]),
        new([new("row", Scope), new("zero", Number), new("nan", Number), new("alias", Number)]),
        new([new("nan", Number), new("zero", Number)]),
        new([new("nan", Number), new("branch", Bool), new("zero", Number)]),
        new([new("selected", Scope), new("prior", Scope)]),
        new([new("zero", Number), new("nan", Number)]),
        new([new("zero", Number)]),
        new([new("single", Scope), new("packed", Scope)]),
        new([new("prefix", Number), new("first", OptionalScope), new("second", OptionalScope), new("current", Number)])
    ];
    private static readonly Value[] Constants = [
        new NumberValue(0), new NumberValue(BitConverter.Int64BitsToDouble(Fixtures.NegativeNaN)),
        new NumberValue(1), new NumberValue(2), new StringValue("scope"), new NumberValue(99)
    ];

    internal static async Task Run(string root, bool metrics, Func<Module, string, IEnumerable<Module>> routes)
    {
        foreach (Module image in routes(SuspendedSnapshot(), root))
        {
            RuntimeProfile profile = new(instructionSites: true);
            using GatedLineInput input = new();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            Task<Value> pending = Interpreter.ExecuteAsync(image, new(WorkingDirectory: root)
                { Input = input, Profile = profile }, deadline.Token).AsTask();
            Task first = await Task.WhenAny(input.Requested, pending).WaitAsync(deadline.Token);
            if (first == pending) _ = await pending;
            Require(first == input.Requested && !pending.IsCompleted, "Later field evaluation did not suspend.");
            input.Release();
            var result = (ScopeValue)await pending.WaitAsync(deadline.Token);
            var row = (ScopeValue)result["row"];
            Bits(row["zero"], long.MinValue, "first field is the original negative zero");
            Bits(row["nan"], Fixtures.NegativeNaN, "second field retains original NaN sign and payload");
            Bits(result["zero"], 0, "local mutation updated the source slot, not its earlier field");
            Bits(result["nan"], Fixtures.NegativeNaN ^ long.MinValue, "local NaN mutation remains visible");
            Require(row["line"] is StringValue { Data: "release" }, "Later field was evaluated once after release.");
            Bits(row["shared"], BitConverter.DoubleToInt64Bits(99), "stored Cell sees subsequent local mutation");
            row.SetMember("shared", new NumberValue(113));
            Require(result["alias"] is NumberValue { Data: 113 }, "Both fields retained the same captured Cell.");
            CheckSites(image, profile, metrics, rawLoads: 2, storageVisits: 4);
        }

        foreach (bool selected in new[] { false, true })
        {
            foreach (Module image in routes(BranchAndDuplicate(), root))
            {
                var (value, profile) = await RunSelected(image, root, selected);
                var result = (ScopeValue)value;
                var selectedRow = (ScopeValue)result["selected"];
                Bits(selectedRow["nan"], Fixtures.NegativeNaN, "field after branch merge retained NaN bits");
                Bits(((ScopeValue)result["prior"])["nan"], Fixtures.NegativeNaN, "duplicated first field retained NaN bits");
                Bits(selectedRow["zero"], long.MinValue, "the second numeric field retained its sign");
                Require(selectedRow["branch"] is BoolValue decision && decision.Data == selected, "Wrong branch selected.");
                CheckSites(image, profile, metrics, rawLoads: 1, storageVisits: 1);
            }
            foreach (Module image in routes(AlternativeReturn(), root))
            {
                var (value, profile) = await RunSelected(image, root, selected);
                Bits(selected ? ((ScopeValue)value)["zero"] : value, long.MinValue, "mixed consumer fallback");
                CheckSites(image, profile, metrics, rawLoads: 0, storageVisits: 1);
            }
            foreach (Module image in routes(DirectSuffix(), root))
            {
                var (value, profile) = await RunSelected(image, root, selected);
                if (selected) Bits(value, 0, "the earlier storage load still reaches generic Return");
                else Require(value is NullValue, "The constructor must not consume the earlier Null operand.");
                // With a nonnumeric prefix both raw loads must come from the
                // constructor. Making that prefix raw must not add a third.
                CheckSites(image, profile, metrics, rawLoads: 2, storageVisits: 3);
            }
        }

        foreach (Module image in routes(Singleton(), root))
        {
            RuntimeProfile profile = new(instructionSites: true);
            var result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
            Bits(((ScopeValue)result["single"])["zero"], long.MinValue, "singleton stays on ordinary Object construction");
            Bits(((ScopeValue)result["packed"])["nan"], Fixtures.NegativeNaN, "two-field constructor remains eligible");
            CheckSites(image, profile, metrics, rawLoads: 1, storageVisits: 2);
        }

        foreach (Module image in routes(LoopRows(), root))
        {
            RuntimeProfile profile = new(instructionSites: true);
            var result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
            var first = (ScopeValue)result["first"];
            var second = (ScopeValue)result["second"];
            Bits(first["zero"], long.MinValue, "first row has the initial sign");
            Bits(first["nan"], Fixtures.NegativeNaN, "first row has the initial NaN");
            Bits(second["zero"], 0, "second row has the changed sign");
            Bits(second["nan"], Fixtures.NegativeNaN ^ long.MinValue, "second row has the changed NaN sign");
            Bits(result["prefix"], Fixtures.NegativeNaN, "one operand survives the loop backedge");
            Bits(result["current"], Fixtures.NegativeNaN ^ long.MinValue, "the source changes after both rows");
            first.SetMember("zero", new NumberValue(37));
            Bits(second["zero"], 0, "changing one row cannot change another row");
            CheckSites(image, profile, metrics, rawLoads: 5, storageVisits: 5);
        }

        Console.WriteLine("PASS Object storage transport: suspended snapshot, Cell aliases, duplicated CFG merge, generic return fallback, direct suffix and loop rows");
    }

    private static Module SuspendedSnapshot() => Image([
        .. Seed(),
        new(OpCode.LoadCell, Counter, Number + 1), new(OpCode.Pop),
        new(OpCode.LoadStorage, ZeroSlot), new(OpCode.LoadStorage, NaNSlot), new(OpCode.LoadStorage, Counter),
        .. FlipBoth(),
        new(OpCode.NativeCall, Text: "io.read_line"),
        new(OpCode.NewScope, SnapshotRow), new(OpCode.InitializeLocal, 3),
        new(OpCode.Constant, NewCellValue), new(OpCode.StoreLocal, Counter),
        new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, ZeroSlot), new(OpCode.LoadLocal, NaNSlot),
        new(OpCode.LoadStorage, Counter), new(OpCode.NewScope, SnapshotResult),
        new(OpCode.Return)
    ], locals: 4);

    private static Module BranchAndDuplicate()
    {
        List<Instruction> code = [
            .. Seed(), new(OpCode.LoadStorage, NaNSlot), new(OpCode.Dup),
            new(OpCode.LoadLocal, ZeroSlot), new(OpCode.NewScope, NaNZero), new(OpCode.InitializeLocal, 3),
            .. ReadSelection()
        ];
        int alternative = code.Count;
        code.AddRange([new(OpCode.JumpIfFalse, alternative + 3), new(OpCode.True),
            new(OpCode.Jump, alternative + 4), new(OpCode.False),
            new(OpCode.LoadLocal, ZeroSlot), new(OpCode.NewScope, BranchRow), new(OpCode.LoadLocal, 3),
            new(OpCode.NewScope, BranchResult), new(OpCode.Return)]);
        return Image([.. code], locals: 4);
    }

    private static Module AlternativeReturn()
    {
        List<Instruction> code = [.. Seed(), new(OpCode.LoadStorage, ZeroSlot), .. ReadSelection()];
        int alternative = code.Count;
        code.AddRange([new(OpCode.JumpIfFalse, alternative + 4), new(OpCode.LoadLocal, NaNSlot),
            new(OpCode.NewScope, ZeroNaN),
            new(OpCode.Return), new(OpCode.Return)]);
        return Image([.. code], locals: 3);
    }

    private static Module Singleton() => Image([
        .. Seed(), new(OpCode.LoadStorage, ZeroSlot), new(OpCode.NewScope, ZeroOnly),
        new(OpCode.LoadStorage, NaNSlot), new(OpCode.LoadLocal, ZeroSlot), new(OpCode.NewScope, NaNZero),
        new(OpCode.NewScope, SingletonResult), new(OpCode.Return)
    ], locals: 3);

    private static Module DirectSuffix()
    {
        List<Instruction> code = [.. Seed(), .. ReadSelection()];
        int branch = code.Count;
        code.AddRange([
            new(OpCode.JumpIfTrue, branch + 3), new(OpCode.Null), new(OpCode.StoreLocal, Counter),
            new(OpCode.LoadStorage, Counter), new(OpCode.LoadStorage, ZeroSlot), new(OpCode.LoadStorage, NaNSlot),
            new(OpCode.NewScope, ZeroNaN), new(OpCode.Pop), new(OpCode.Return)
        ]);
        return Image([.. code], locals: 3);
    }

    private static Module LoopRows()
    {
        List<Instruction> code = [
            .. Seed(), new(OpCode.Null), new(OpCode.InitializeLocal, 3),
            new(OpCode.Null), new(OpCode.InitializeLocal, 4), new(OpCode.LoadStorage, NaNSlot)
        ];
        int loop = code.Count;
        code.AddRange([
            new(OpCode.LoadStorage, ZeroSlot), new(OpCode.LoadStorage, NaNSlot), .. FlipBoth(),
            new(OpCode.LoadLocal, Counter), new(OpCode.Constant, Zero), new(OpCode.Equal)
        ]);
        int second = code.Count;
        code.AddRange([
            new(OpCode.JumpIfFalse, second + 4), new(OpCode.NewScope, ZeroNaN),
            new(OpCode.InitializeLocal, 3), new(OpCode.Jump, second + 6),
            new(OpCode.NewScope, ZeroNaN), new(OpCode.InitializeLocal, 4),
            new(OpCode.LoadLocal, Counter), new(OpCode.Constant, One), new(OpCode.Add),
            new(OpCode.StoreLocal, Counter), new(OpCode.LoadLocal, Counter), new(OpCode.Constant, Two),
            new(OpCode.Less), new(OpCode.JumpIfTrue, loop),
            new(OpCode.LoadLocal, NaNSlot), new(OpCode.Negate), new(OpCode.StoreLocal, NaNSlot),
            new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, NaNSlot),
            new(OpCode.NewScope, LoopResult), new(OpCode.Return)
        ]);
        return Image([.. code], locals: 5);
    }

    private static Instruction[] Seed() => [
        new(OpCode.Constant, Zero), new(OpCode.Negate), new(OpCode.InitializeLocal, ZeroSlot),
        new(OpCode.Constant, NaN), new(OpCode.Negate), new(OpCode.InitializeLocal, NaNSlot),
        new(OpCode.LoadLocal, NaNSlot), new(OpCode.Negate), new(OpCode.StoreLocal, NaNSlot),
        new(OpCode.Constant, Zero), new(OpCode.Constant, Zero), new(OpCode.Add), new(OpCode.InitializeLocal, Counter)
    ];

    private static Instruction[] FlipBoth() => [
        new(OpCode.LoadLocal, ZeroSlot), new(OpCode.Negate), new(OpCode.StoreLocal, ZeroSlot),
        new(OpCode.LoadLocal, NaNSlot), new(OpCode.Negate), new(OpCode.StoreLocal, NaNSlot)
    ];

    private static Instruction[] ReadSelection() => [
        new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Constant, SelectScope), new(OpCode.Equal)
    ];

    private static Module Image(Instruction[] code, int locals) =>
        new(Constants, [new FunctionCode("scope-storage", 0, locals, code, TypeRef.Any)
            { CodeSignature = new([], Any, []) }], OptimizationLevel: 0)
        { CodeTypes = Types, ScopeLayouts = Layouts };

    private static async Task<(Value Value, RuntimeProfile Profile)> RunSelected(Module image, string root, bool selected)
    {
        RuntimeProfile profile = new(instructionSites: true);
        using StringReader input = new(selected ? "scope\n" : "return\n");
        var value = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Input = input, Profile = profile });
        return (value, profile);
    }

    private static void CheckSites(Module image, RuntimeProfile profile, bool metrics, ulong rawLoads, ulong storageVisits)
    {
        var samples = profile.Drain();
        var sites = samples.Where(sample => sample.Name == "vm.instruction" && sample.Attributes["opcode"] == "LoadStorage");
        ulong visits = 0;
        foreach (var site in sites)
        {
            int pc = int.Parse(site.Attributes["instruction.pc"], System.Globalization.CultureInfo.InvariantCulture);
            Require(image.Functions[0].Instructions[pc].Op == OpCode.LoadStorage, "The storage PC changed in profiling.");
            visits += site.Metrics["hits"];
        }
        Require(visits == storageVisits, "Logical LoadStorage visits changed.");
        if (!metrics) return;
        var function = samples.Single(sample => sample.Name == "vm.function").Metrics;
        Require(function.GetValueOrDefault("raw_local_writes") >= 4, "The fixture did not use private Number locals.");
        Require(function.GetValueOrDefault("raw_scope_storage_loads") == rawLoads,
            $"Expected {rawLoads} raw Object storage loads, saw {function.GetValueOrDefault("raw_scope_storage_loads")}.");
    }

    private static void Bits(Value value, long bits, string label) =>
        Require(value is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == bits,
            $"{label}: expected Number bits {bits:x16}, got {value}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
