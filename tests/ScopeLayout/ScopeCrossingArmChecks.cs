using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;

internal static class ScopeCrossingArmChecks
{
    private static readonly SourceSpan BxSpan = new("crossing-fixture", 41, 7);
    private const int Any = 0, Number = 1, Boolean = 2, Receiver = 3, OpaqueReceiver = 4;
    private const int Natural = 0, Reordered = 1, Incomplete = 2, Alias = 3, Result = 4;
    private const int Low = 0, High = 1, StartX = 2, StopX = 3, OpaqueOffset = 4, CapturedValue = 8;
    private enum Variant { Positive, Negative, Outside, NaN, SignedZero, MissingLazy, MissingSelected, Permuted, Cell, Live, DirectEntry }

    internal static async Task Run()
    {
        string cwd = Environment.CurrentDirectory;
        foreach (var variant in Enum.GetValues<Variant>())
        {
            var (module, prefix, entry, join) = Fixture(variant);
            var portable = RoundTrip(module);
            var bound = RoundTrip(Binding.Relocate(portable, new(cwd, cwd)));
            var plan = ExecutionTargets.For(bound).ScopeRegion(1, prefix).Crossing;
            if (variant == Variant.Live) Require(plan is null, "A live-out StoreLocal was omitted.");
            else Require(plan is { DeadStores.Length: 4 } && plan.Entry == entry && plan.Exit == join,
                $"The arm plan must be derived from the original entry and exit PCs: {variant}.");

            foreach (var image in new[] { portable, bound })
            {
                RuntimeProfile counted = new();
                RuntimeProfile detailed = new(instructionSites: true);
                await CheckResult(image, variant, null);
                await CheckResult(image, variant, counted);
                await CheckResult(image, variant, detailed);

                var summary = counted.Drain();
                var instructions = detailed.Drain();
                var counters = FunctionCounters(summary);
                var detailedCounters = FunctionCounters(instructions);
                bool hit = variant is Variant.Positive or Variant.Negative or Variant.Outside or Variant.NaN
                    or Variant.SignedZero or Variant.Permuted;
                bool miss = variant is Variant.MissingSelected or Variant.Cell;
                Require(counters["guarded_scope_regions"] == (variant == Variant.DirectEntry ? 0UL : 1UL)
                    && counters["guarded_scope_misses"] == 0
                    && counters["guarded_scope_crossings"] == (hit ? 1UL : 0UL)
                    && counters["guarded_scope_crossing_misses"] == (miss ? 1UL : 0UL),
                    $"Unexpected prefix/crossing path for {variant}/{image.Stage}: {string.Join(", ", counters)}.");
                Require(detailedCounters["guarded_scope_crossings"] == 0 && detailedCounters["guarded_scope_crossing_misses"] == 0,
                    "Instruction-site observations must remain canonical.");
                Require(OpCounts(summary).SequenceEqual(OpCounts(instructions)),
                    $"Counted arm must replay the exact canonical path for {variant}/{image.Stage}.");
            }
        }
        Console.WriteLine("PASS whole guarded Scope crossing: 22 artifact routes, both branch paths, lazy X projection, Cell fallback, exact counters");
    }

    private static IReadOnlyDictionary<string, ulong> FunctionCounters(IReadOnlyList<RuntimeProfileSample> samples) =>
        samples.Single(sample => sample.Name == "vm.function" && sample.Attributes["function.id"] == "1").Metrics;

    private static async Task CheckResult(Module image, Variant variant, RuntimeProfile? profile)
    {
        try
        {
            var result = await Interpreter.ExecuteAsync(image, new() { Profile = profile });
            Require(variant != Variant.MissingSelected, "The selected missing field did not fail.");
            if (variant is Variant.Cell or Variant.Live)
            {
                Require(result is ScopeValue row && row["crossed"] is BoolValue { Data: true }
                    && row["observed"] is NumberValue { Data: -1 }, "The canonical store changed an observed location.");
                return;
            }
            bool expected = variant is not (Variant.Outside or Variant.NaN or Variant.MissingLazy);
            Require(result is BoolValue boolean && boolean.Data == expected, $"Incorrect result for {variant}/{image.Stage}.");
        }
        catch (MsyncException error) when (variant == Variant.MissingSelected)
        {
            Require(error.Span == BxSpan && error.Message == "Scope has no member 'stop_x'",
                "A late guard miss must preserve the selected member failure and original span.");
        }
    }

    private static IEnumerable<(string Opcode, ulong Visits)> OpCounts(IReadOnlyList<RuntimeProfileSample> samples) =>
        samples.Where(sample => sample.Name == "vm.opcode")
            .Select(sample => (sample.Attributes["opcode"], sample.Metrics["hits"]));

    private static (Module Module, int Prefix, int Entry, int Join) Fixture(Variant variant)
    {
        const int receiver = 0, x = 1, y = 2, ay = 3, by = 4, ax = 5, dy = 6, lhs = 7, rhs = 8, alias = 9;
        const int xValue = 4, yValue = 5, zero = 6, seed = 7;
        bool reverse = variant == Variant.Negative;
        bool missing = variant is Variant.MissingLazy or Variant.MissingSelected;
        bool permuted = variant == Variant.Permuted;
        int Field(int id) => missing ? id + OpaqueOffset : id;
        Value[] constants =
        [
            new NumberValue(reverse ? 4 : -1), new NumberValue(reverse ? 2 : -3),
            new NumberValue(reverse ? -1 : 4), new NumberValue(reverse ? -3 : 2),
            new NumberValue(variant switch { Variant.Outside => 3, Variant.NaN => double.NaN, Variant.SignedZero => -0d, _ => 0 }),
            new NumberValue(variant == Variant.MissingLazy ? 5 : 0), new NumberValue(0), new NumberValue(77),
        ];
        List<Instruction> code =
        [
            new(OpCode.Constant, xValue), new(OpCode.InitializeLocal, x),
            new(OpCode.Constant, yValue), new(OpCode.InitializeLocal, y),
        ];
        if (variant is Variant.Cell or Variant.Live) code.AddRange([new(OpCode.Constant, seed), new(OpCode.InitializeLocal, ax)]);
        if (variant == Variant.Cell)
            code.AddRange([new(OpCode.LoadCell, ax, Number + 1), new(OpCode.NewScope, Alias), new(OpCode.InitializeLocal, alias)]);
        int alternate = -1;
        if (variant == Variant.DirectEntry)
        {
            code.AddRange([new(OpCode.Constant, 1), new(OpCode.InitializeLocal, ay), new(OpCode.Constant, 3),
                new(OpCode.InitializeLocal, by), new(OpCode.True)]);
            alternate = code.Count;
            code.Add(new(OpCode.JumpIfTrue));
        }
        int prefix = code.Count;
        code.AddRange([
            new(OpCode.LoadLocal, receiver), new(OpCode.LoadField, Field(Low)), new(OpCode.StoreLocal, ay),
            new(OpCode.LoadLocal, receiver), new(OpCode.LoadField, Field(High)), new(OpCode.StoreLocal, by),
            new(OpCode.LoadLocal, ay), new(OpCode.LoadLocal, y), new(OpCode.Greater),
            new(OpCode.LoadLocal, by), new(OpCode.LoadLocal, y), new(OpCode.Greater), new(OpCode.Equal),
        ]);
        int crossing = code.Count;
        code.AddRange([new(OpCode.JumpIfFalse), new(OpCode.False), new(OpCode.Jump)]);
        int entry = code.Count;
        code[crossing] = code[crossing] with { A = entry };
        if (alternate >= 0) code[alternate] = code[alternate] with { A = entry };
        code.AddRange([
            new(OpCode.LoadLocal, receiver), new(OpCode.LoadField, Field(StartX)), new(OpCode.StoreLocal, ax),
            new(OpCode.LoadLocal, by), new(OpCode.LoadLocal, ay), new(OpCode.Subtract), new(OpCode.StoreLocal, dy),
            new(OpCode.LoadLocal, x), new(OpCode.LoadLocal, ax), new(OpCode.Subtract),
            new(OpCode.LoadLocal, dy), new(OpCode.Multiply), new(OpCode.StoreLocal, lhs),
            new(OpCode.LoadLocal, receiver), new(OpCode.LoadField, Field(StopX), Span: BxSpan),
            new(OpCode.LoadLocal, ax), new(OpCode.Subtract), new(OpCode.LoadLocal, y), new(OpCode.LoadLocal, ay),
            new(OpCode.Subtract), new(OpCode.Multiply), new(OpCode.StoreLocal, rhs),
            new(OpCode.LoadLocal, dy), new(OpCode.Constant, zero), new(OpCode.Greater),
        ]);
        int direction = code.Count;
        code.AddRange([
            new(OpCode.JumpIfFalse), new(OpCode.LoadLocal, lhs), new(OpCode.LoadLocal, rhs), new(OpCode.Less), new(OpCode.Jump),
        ]);
        code[direction] = code[direction] with { A = code.Count };
        code.AddRange([new(OpCode.LoadLocal, lhs), new(OpCode.LoadLocal, rhs), new(OpCode.Greater)]);
        int join = code.Count;
        code[crossing + 2] = code[crossing + 2] with { A = join };
        code[direction + 4] = code[direction + 4] with { A = join };
        if (variant == Variant.Cell)
            code.AddRange([new(OpCode.LoadLocal, alias), new(OpCode.LoadField, CapturedValue)]);
        else if (variant == Variant.Live) code.Add(new(OpCode.LoadLocal, ax));
        if (variant is Variant.Cell or Variant.Live) code.Add(new(OpCode.NewScope, Result));
        code.Add(new(OpCode.Return));
        var entryPoint = new FunctionCode("entry", 0, 0, [
            new(OpCode.MakeClosure, 1),
            new(OpCode.Constant, permuted ? 3 : 0), new(OpCode.Constant, permuted ? 2 : 1),
            new(OpCode.Constant, permuted ? 1 : 2), new(OpCode.Constant, permuted ? 0 : 3),
            new(OpCode.NewScope, missing ? Incomplete : permuted ? Reordered : Natural),
            new(OpCode.Call, 1, 2), new(OpCode.Return)
        ], TypeRef.Any) { CodeSignature = new([], Any, []) };
        var crossingBody = new FunctionCode("crossing", 1, 10, [.. code], TypeRef.Any, ParameterTypes: [TypeRef.Scope])
            { CodeSignature = new([missing ? OpaqueReceiver : Receiver], Any, []) };
        var module = new Module(constants, [entryPoint, crossingBody], OptimizationLevel: 0)
        {
            CodeTypes = [
                new(TypeKind.Any), new(TypeKind.Number), new(TypeKind.Bool),
                new(TypeKind.Scope, ScopeLayouts: [Natural, Reordered]), new(TypeKind.Scope)
            ],
            ScopeLayouts = [
                new([new("start_x", Number), new("low", Number), new("stop_x", Number), new("high", Number)]),
                new([new("high", Number), new("stop_x", Number), new("low", Number), new("start_x", Number)]),
                new([new("start_x", Number), new("low", Number), new("unused", Number), new("high", Number)]),
                new([new("value", Number)]),
                new([new("crossed", Boolean), new("observed", Number)])
            ],
            FieldProjections = [
                new("low", [new(Natural, 1), new(Reordered, 2)]),
                new("high", [new(Natural, 3), new(Reordered, 0)]),
                new("start_x", [new(Natural, 0), new(Reordered, 3)]),
                new("stop_x", [new(Natural, 2), new(Reordered, 1)]),
                new("low", []), new("high", []), new("start_x", []), new("stop_x", []),
                new("value", [new(Alias, 0)])
            ]
        };
        return (module, prefix, entry, join);
    }

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
