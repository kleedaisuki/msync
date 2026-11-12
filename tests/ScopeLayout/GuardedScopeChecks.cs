using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;

internal static class GuardedScopeChecks
{
    private const long NaNBits = unchecked((long)0xfff8000000004a01UL);
    private static readonly SourceSpan FirstField = new("guarded-scope", 11, 5);
    private const int Any = 0, Number = 1, Boolean = 2, Pair = 3, PairResult = 4, OpaqueScope = 5, VersionResult = 6;
    private const int Natural = 0, Permuted = 1, Alias = 2, Compared = 3, Tagged = 4, Versions = 5, LowOnly = 6;
    private const int Low = 0, High = 1, Aliased = 2, Absent = 3, OpaqueHigh = 4;

    internal static async Task Run()
    {
        Require(!Enum.IsDefined(ExecutionTargets.ScopeComparison), "ScopeComparison is not a portable opcode.");
        foreach (var (left, right, threshold, same) in new[]
        {
            (1d, -1d, 0d, false),
            (-0d, 0d, 0d, true),
            (BitConverter.Int64BitsToDouble(NaNBits), 2d, 0d, false)
        })
        {
            var (image, start) = Fixture(left, right, threshold);
            Require(ExecutionTargets.For(image).Dispatch(0)?[start].Op == ExecutionTargets.ScopeComparison
                && image.Functions[0].Instructions[start].Op == OpCode.LoadLocal,
                "The bound view replaces only the original entry.");
            CheckResult(await Interpreter.ExecuteAsync(image), same, left, right);
            await CheckObserved(image, start, same, left, right, hits: 1, misses: 0);
        }

        var privateLane = Fixture(-0d, 1d, 0, privateLane: true);
        await CheckObserved(privateLane.Image, privateLane.Start, false, -0d, 1, hits: 1, misses: 0, numericCommits: 2);
        await CheckBranchExits();
        await CheckLayoutVersions();
        var reordered = Fixture(4, -1, 0, permuted: true);
        await CheckObserved(reordered.Image, reordered.Start, false, 4, -1, hits: 1, misses: 0);
        var cell = Fixture(6, 3, 7, captureDestination: true);
        await CheckObserved(cell.Image, cell.Start, true, 6, 3, hits: 0, misses: 1);
        var interior = Fixture(-1, -2, 0, interiorEntry: true);
        await CheckObserved(interior.Image, interior.Start, false, 29, -2, hits: 0, misses: 0);

        var missing = MissingField();
        foreach (RuntimeProfile? profile in new RuntimeProfile?[] { null, new() })
        {
            try
            {
                _ = await Interpreter.ExecuteAsync(missing, new() { Profile = profile });
                throw new InvalidOperationException("A missing guarded field unexpectedly resolved.");
            }
            catch (MsyncException error)
            {
                Require(error.Span == FirstField && error.Message == "Scope has no member 'absent'",
                    "A guard miss must leave the original failing field and span selected.");
            }
        }
        await CheckDeferred();
        Console.WriteLine("PASS guarded Scope comparison, branch exits and dead stores, counted/detailed PCs, signed zero/NaN, Cell, interior entry and deferred fallback");
    }

    private static async Task CheckLayoutVersions()
    {
        const int initial = 0, threshold = 1, updated = 2, permutedLow = 3, permutedHigh = 4;
        var caller = new FunctionCode("main", 0, 2,
        [
            new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 1),
            new(OpCode.Constant, initial), new(OpCode.Constant, initial), new(OpCode.NewScope, Natural),
            new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, 2),
            new(OpCode.LoadLocal, 0), new(OpCode.Constant, updated), new(OpCode.StoreField, High), new(OpCode.Pop),
            new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, 2),
            new(OpCode.LoadLocal, 1), new(OpCode.Constant, permutedHigh), new(OpCode.Constant, permutedLow),
            new(OpCode.NewScope, Permuted), new(OpCode.Call, 1, 2),
            new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, 2),
            new(OpCode.NewScope, Versions), new(OpCode.Return)
        ], TypeRef.Scope) { CodeSignature = new([], VersionResult, []) };
        var compare = new FunctionCode("compare", 1, 4,
        [
            new(OpCode.Constant, threshold), new(OpCode.InitializeLocal, 3), .. Region(),
            new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2),
            new(OpCode.NewScope, Compared), new(OpCode.Return)
        ], TypeRef.Scope, ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([Pair], PairResult, []) };
        Module portable = RoundTrip(Image([new NumberValue(-1), new NumberValue(0), new NumberValue(4),
            new NumberValue(-4), new NumberValue(3)], caller, compare) with { Stage = ArtifactStage.Relocatable, Context = null });
        string cwd = Environment.CurrentDirectory;
        Module bound = RoundTrip(Binding.Relocate(portable, new(cwd, cwd)));
        foreach (var image in new[] { portable, bound })
        {
            RuntimeProfile profile = new();
            var result = (ScopeValue)await Interpreter.ExecuteAsync(image, new() { Profile = profile });
            AssertProjection("first", true, -1, -1);
            AssertProjection("changed", false, -1, 4);
            AssertProjection("permuted", false, -4, 3);
            AssertProjection("revisited", false, -1, 4);
            var counts = profile.Drain().Single(sample => sample.Name == "vm.function"
                && sample.Attributes["function.id"] == "1").Metrics;
            Require(counts["guarded_scope_regions"] == 4 && counts["guarded_scope_misses"] == 0,
                "One site must use both admitted field orderings without falling back by name.");

            void AssertProjection(string name, bool same, double left, double right)
            {
                var row = (ScopeValue)result[name];
                Require(row["same"] is BoolValue comparison && comparison.Data == same
                    && row["left"] is NumberValue l && l.Data == left
                    && row["right"] is NumberValue r && r.Data == right,
                    $"Layout proof read stale values or changed name resolution: {name}/{image.Stage}.");
            }
        }
    }

    private static Module RoundTrip(Module image)
    {
        using MemoryStream stream = new();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }

    private static async Task CheckBranchExits()
    {
        foreach (var branch in new[] { OpCode.JumpIfTrue, OpCode.JumpIfFalse })
            foreach (bool equal in new[] { false, true })
            {
                double right = equal ? 0 : 1;
                var (image, start) = Fixture(-0d, right, 0, privateLane: true, branch: branch);
                int branchPc = start + GuardedScopeRegion.Width;
                var exit = ExecutionTargets.For(image).ScopeRegion(0, start).Exit;
                Require(exit is { TakenWrites: 1, FallthroughWrites: 2 }
                    && exit.Value.OnTrue == (branch == OpCode.JumpIfTrue),
                    "The selected exit should commit only its surviving destination.");
                bool taken = equal == (branch == OpCode.JumpIfTrue);
                double expectedLeft = taken ? -0d : 29d;
                double expectedRight = taken ? 29d : right;
                CheckResult(await Interpreter.ExecuteAsync(image), taken, expectedLeft, expectedRight);
                var sites = await CheckObserved(image, start, taken, expectedLeft, expectedRight,
                    hits: 1, misses: 0, numericCommits: 1);
                int target = taken ? image.Functions[0].Instructions[branchPc].A : branchPc + 1;
                string sourcePc = branchPc.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var edge = sites.Single(sample => sample.Name == "vm.edge" && sample.Attributes["instruction.pc"] == sourcePc);
                Require(edge.Attributes["opcode"] == branch.ToString()
                    && edge.Attributes["target.pc"] == target.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && edge.Metrics["hits"] == 1, "The original conditional PC and exact successor must be retained.");
            }
    }

    private static async Task<IReadOnlyList<RuntimeProfileSample>> CheckObserved(Module image, int start, bool same, double left, double right,
        ulong hits, ulong misses, ulong numericCommits = 0)
    {
        RuntimeProfile counted = new();
        RuntimeProfile detailed = new(instructionSites: true);
        CheckResult(await Interpreter.ExecuteAsync(image, new() { Profile = counted }), same, left, right);
        CheckResult(await Interpreter.ExecuteAsync(image, new() { Profile = detailed }), same, left, right);
        var counts = counted.Drain();
        var sites = detailed.Drain();
        var counters = counts.Single(sample => sample.Name == "vm.function").Metrics;
        Require(counters["guarded_scope_regions"] == hits && counters["guarded_scope_misses"] == misses,
            "The selected observation mode used the wrong region lane.");
        Require(counters["numeric_local_commits"] == numericCommits, "Region stores did not use the expected Number lane.");
        Require(sites.Single(sample => sample.Name == "vm.function").Metrics["guarded_scope_regions"] == 0,
            "Detailed instruction observation must remain canonical.");
        Require(OpCounts(counts).SequenceEqual(OpCounts(sites)), "Aggregate observation changed canonical op counts.");
        bool visited = sites.Any(sample => sample.Name == "vm.instruction"
            && sample.Attributes["instruction.pc"] == start.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Require(visited == (hits + misses != 0), "Canonical tracing visited the wrong entry PC.");
        return sites;
    }

    private static IEnumerable<(string Op, ulong Hits)> OpCounts(IReadOnlyList<RuntimeProfileSample> samples) =>
        samples.Where(sample => sample.Name == "vm.opcode")
            .Select(sample => (sample.Attributes["opcode"], sample.Metrics["hits"]));

    private static (Module Image, int Start) Fixture(double left, double right, double threshold,
        bool permuted = false, bool captureDestination = false, bool interiorEntry = false,
        bool privateLane = false, OpCode? branch = null)
    {
        Value[] values = [new NumberValue(left), new NumberValue(right), new NumberValue(threshold), new NumberValue(29)];
        List<Instruction> code = [
            new(OpCode.Constant, permuted ? 1 : 0), new(OpCode.Constant, permuted ? 0 : 1),
            new(OpCode.NewScope, permuted ? Permuted : Natural), new(OpCode.InitializeLocal, 0),
            new(OpCode.Constant, 2), new(OpCode.InitializeLocal, 3),
            new(OpCode.Constant, 3), new(OpCode.InitializeLocal, 1),
            new(OpCode.Constant, 3), new(OpCode.InitializeLocal, 2)
        ];
        if (captureDestination)
            code.AddRange([new(OpCode.LoadCell, 1, Number + 1), new(OpCode.NewScope, Alias), new(OpCode.InitializeLocal, 4)]);
        if (privateLane)
            for (int i = 0; i < 4; i++)
                code.AddRange([new(OpCode.Constant, 2), new(OpCode.Negate), new(OpCode.InitializeLocal, 4)]);
        code.Add(new(OpCode.Constant, 3));
        if (interiorEntry)
        {
            code.AddRange([new(OpCode.Constant, 3), new(OpCode.NewScope, LowOnly)]);
            code.Add(new(OpCode.Jump, code.Count + 2));
        }
        int start = code.Count;
        code.AddRange(Region());
        if (branch is { } conditional)
        {
            int branchPc = code.Count;
            code.Add(new(conditional));
            ExitArm(code, taken: false);
            code[branchPc] = code[branchPc] with { A = code.Count };
            ExitArm(code, taken: true);
        }
        else
        {
            if (captureDestination) code.AddRange([new(OpCode.LoadLocal, 4), new(OpCode.LoadField, Aliased)]);
            else code.Add(new(OpCode.LoadLocal, 1));
            code.AddRange([new(OpCode.LoadLocal, 2), new(OpCode.NewScope, Tagged), new(OpCode.Return)]);
        }
        return (RoundTrip(Image(values, Entry([.. code]))), start);
    }

    private static void ExitArm(List<Instruction> code, bool taken) => code.AddRange([
        new(OpCode.Constant, 3), new(OpCode.StoreLocal, taken ? 2 : 1), new(taken ? OpCode.True : OpCode.False),
        new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2),
        new(OpCode.NewScope, Tagged), new(OpCode.Return)
    ]);

    private static Module MissingField() => RoundTrip(Image([new NumberValue(4), new NumberValue(-1), new NumberValue(0)],
        Entry([
            new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1),
            new(OpCode.NewScope, Natural), new(OpCode.Call, 1, 2), new(OpCode.Return)
        ]),
        new FunctionCode("opaque compare", 1, 4, [
            new(OpCode.Constant, 2), new(OpCode.InitializeLocal, 3),
            .. Region(Absent, OpaqueHigh), new(OpCode.Return)
        ], TypeRef.Any, ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([OpaqueScope], Any, []) }));

    private static async Task CheckDeferred()
    {
        TaskCompletionSource<ProcessValue> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = Image([new ProcessValue(0, "", "") { Completion = exit.Task }, new NumberValue(-1), new NumberValue(0)],
        Entry([
            new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "process.exit_code"),
            new(OpCode.Constant, 1), new(OpCode.NewScope, Natural), new(OpCode.InitializeLocal, 0),
            new(OpCode.Constant, 2), new(OpCode.InitializeLocal, 3),
            .. Region(), new(OpCode.Return)
        ]));
        try
        {
            RuntimeProfile profile = new();
            var pending = Interpreter.ExecuteAsync(module, new() { Profile = profile });
            Require(!pending.IsCompleted, "A selected deferred comparison must remain pending.");
            exit.SetResult(new(4, "", ""));
            Require(await pending is BoolValue { Data: false }, "Fallback must compare the eventual Number.");
            Require(profile.Drain().Single(sample => sample.Name == "vm.function").Metrics["guarded_scope_misses"] == 1,
                "The deferred payload did not use the canonical comparison path.");
        }
        finally { exit.TrySetResult(new(4, "", "")); }
    }

    private static Instruction[] Region(int left = Low, int right = High) => [
        new(OpCode.LoadLocal, 0), new(OpCode.LoadField, left, Span: FirstField), new(OpCode.StoreLocal, 1),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadField, right), new(OpCode.StoreLocal, 2),
        new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 3), new(OpCode.Greater),
        new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3), new(OpCode.Greater), new(OpCode.Equal)
    ];

    private static FunctionCode Entry(Instruction[] code) => new("entry", 0, 5, code, TypeRef.Any)
        { CodeSignature = new([], Any, []) };

    private static Module Image(Value[] values, params FunctionCode[] functions)
    {
        string cwd = Environment.CurrentDirectory;
        return new(values, functions, Stage: ArtifactStage.Executable, Context: new(cwd, cwd), OptimizationLevel: 0)
        {
            CodeTypes = [
                new(TypeKind.Any), new(TypeKind.Number), new(TypeKind.Bool),
                new(TypeKind.Scope, ScopeLayouts: [Natural, Permuted]),
                new(TypeKind.Scope, ScopeLayouts: [Compared]), new(TypeKind.Scope),
                new(TypeKind.Scope, ScopeLayouts: [Versions])
            ],
            ScopeLayouts = [
                new([new("low", Number), new("high", Number)]),
                new([new("high", Number), new("low", Number)]),
                new([new("alias", Number)]),
                new([new("same", Boolean), new("left", Number), new("right", Number)]),
                new([new("prefix", Number), new("same", Boolean), new("left", Number), new("right", Number)]),
                new([new("first", PairResult), new("changed", PairResult), new("permuted", PairResult), new("revisited", PairResult)]),
                new([new("low", Number)])
            ],
            FieldProjections = [
                new("low", [new(Natural, 0), new(Permuted, 1), new(LowOnly, 0)]),
                new("high", [new(Natural, 1), new(Permuted, 0)]),
                new("alias", [new(Alias, 0)]),
                new("absent", []),
                new("high", [])
            ]
        };
    }

    private static void CheckResult(Value value, bool same, double left, double right)
    {
        var row = (ScopeValue)value;
        Require(row["prefix"] is NumberValue { Data: 29 } && row["same"] is BoolValue b && b.Data == same,
            "The comparison or lower operand changed.");
        Require(row["left"] is NumberValue l && row["right"] is NumberValue r
            && BitConverter.DoubleToInt64Bits(l.Data) == BitConverter.DoubleToInt64Bits(left)
            && BitConverter.DoubleToInt64Bits(r.Data) == BitConverter.DoubleToInt64Bits(right),
            "The canonical local stores changed the exact Number bits.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
