using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirMachineBlockChecks
{
    private const string SourceName = "ready_signal.msync";
    private const string Expected = "24\n834252\n16\n776783\n16\n621000\n";

    internal static bool ProcessCommand(string[] args)
    {
        switch (args)
        {
            case ["hpir-block-check"]:
                Run();
                return true;
            case ["hpir-block-compile", var source, var artifact]:
                Save(Compile(File.ReadAllText(source), source), artifact);
                return true;
            case ["hpir-block-bind", var artifact, var executable]:
                Save(Bind(Load(artifact)), executable);
                return true;
            case ["hpir-block-run", var executable, "interpreted"]:
                _ = HpirExecutor.Run(Load(executable));
                return true;
            case ["hpir-block-run", var executable, "adaptive"]:
                _ = HpirExecutor.RunAdaptive(Load(executable));
                return true;
            case ["hpir-block-run", var executable, "native"]:
                using (var machine = HpirMachineImage.Compile(Load(executable)))
                    _ = HpirExecutor.RunMachineCandidate(machine);
                return true;
            default:
                return false;
        }
    }

    internal static void Run()
    {
        if (!HpirMachineImage.IsSupported) return;
        using Stream resource = typeof(HpirMachineBlockChecks).Assembly
            .GetManifestResourceStream(SourceName) ?? throw new Exception("Missing ready signal fixture");
        using var source = new StreamReader(resource);
        HpirProgram program = RoundTrip(Bind(RoundTrip(Compile(source.ReadToEnd(), SourceName))));
        var interpretedProfile = new RuntimeProfile(instructionSites: true);
        using var interpretedOutput = new StringWriter { NewLine = "\n" };
        _ = HpirExecutor.Run(program, new(Output: interpretedOutput) { Profile = interpretedProfile });

        var machineProfile = new RuntimeProfile(instructionSites: true);
        using var machineOutput = new StringWriter { NewLine = "\n" };
        List<RuntimeTimelineRecord> timeline = [];
        var observations = new RuntimeTimeline(timeline.Add, "hpir-machine");
        using var machine = HpirMachineImage.Compile(program, observe: true, observations);
        _ = HpirExecutor.RunMachineCandidate(machine, new(Output: machineOutput)
        {
            Profile = machineProfile, Timeline = observations
        });
        Require(interpretedOutput.ToString() == Expected && machineOutput.ToString() == Expected,
            "same lowered HPIR, both execution modes, independent signal oracle");

        var interpreted = interpretedProfile.Drain();
        var native = machineProfile.Drain();
        string[] kinds = ["vm.instruction", "vm.edge", "vm.call", "vm.native"];
        Require(Flow(interpreted, kinds).SequenceEqual(Flow(native, kinds)),
            "identical guest instruction, taken-edge, call and effect counts");
        Require(native.Any(item => item.Metrics.GetValueOrDefault("machine_instructions") > 1000)
            && timeline.Any(item => item.Name == "hpir.machine.emit.function" && item.Attributes.ContainsKey("source.file"))
            && timeline.Any(item => item.Name == "hpir.machine.publish" && item.Metrics.GetValueOrDefault("code.bytes") > 0),
            "actual native work, code publication and function attribution");
        Require(Sum(native, "machine_calls") > 3000 && Sum(native, "machine_returns") == Sum(native, "machine_calls")
            && Sum(native, "machine_entries") < Sum(native, "machine_calls"),
            "ordinary CALL/RET stays native rather than returning to the CLR");
        Console.WriteLine($"signal: native entries={Sum(native, "machine_entries")}; "
            + $"direct calls={Sum(native, "machine_calls")} returns={Sum(native, "machine_returns")}");
        PrimitiveBoundaries();
        ForwardedScalars();
        PhysicalLayoutChecks();
        Cancellation();
        DeepCalls();
        ImportFailure();
        Collection();
        Console.WriteLine("PASS identical HPIR signal image and source-attributed cancellation across native blocks");
    }

    private static void PhysicalLayoutChecks()
    {
        HpirProgram program = RoundTrip(new([
            new HpirFunction("predicate", 24, HpirWidth.U8, [], [
                new(HpirOp.Call, D: 0, A: 0),
                new(HpirOp.ConstU64, D: 8),
                new(HpirOp.Call, D: 16, A: 1),
                new(HpirOp.Call, D: 17, A: 2),
                new(HpirOp.Call, D: 18, A: 3),
                new(HpirOp.OrU8, D: 19, A: 17, B: 18),
                new(HpirOp.XorU8, D: 20, A: 16, B: 19),
                new(HpirOp.Return, A: 20)
            ], [new(~0, []), new(~1, [0]), new(~2, [0]), new(~1, [8])], [])
            { Safepoints = [new(0, [])] }
        ], 0)
        {
            Layouts = [new(8, 8, []), new(8, 8, [])],
            Imports = [
                new(HpirService.AllocateObject, 0, HpirWidth.U64, []),
                new(HpirService.HasLayout, 0, HpirWidth.U8, [HpirWidth.U64]),
                new(HpirService.HasLayout, 1, HpirWidth.U8, [HpirWidth.U64])
            ]
        });
        Require(HpirExecutor.Run(program) == 1, "physical layout identity or null differs");
        var observed = new RuntimeProfile(instructionSites: true);
        using var machine = HpirMachineImage.Compile(program, observe: true);
        Require(HpirExecutor.RunMachineCandidate(machine, new() { Profile = observed }) == 1
            && Sum(observed.Drain(), "machine_inline_calls") == 3,
            "native layout identity uses the heap header, rejects null and retains the typed predicate census");
    }

    private static ulong Sum(IEnumerable<RuntimeProfileSample> profile, string metric) =>
        profile.Aggregate(0UL, (sum, item) => sum + item.Metrics.GetValueOrDefault(metric));

    private static void DeepCalls()
    {
        const string source = """
            fn visit(depth: Number, cell) -> Number {
                if depth <= 0 { return cell::n; };
                let below = visit(depth - 1, cell);
                cell::n = cell::n + 1;
                below + 1
            };
            let counter = object { n: 2 };
            let answer = visit(2048, counter);
            answer * 10000 + counter::n
            """;
        HpirProgram program = RoundTrip(Bind(Compile(source, "machine-segments.msync")));
        var interpreted = new RuntimeProfile();
        ulong expected = HpirExecutor.Run(program, new() { Profile = interpreted });
        var profile = new RuntimeProfile();
        using var machine = HpirMachineImage.Compile(program, observe: true);
        ulong actual = HpirExecutor.RunMachineCandidate(machine, new() { Profile = profile });
        var native = profile.Drain();
        var control = interpreted.Drain();
        Require(BitConverter.UInt64BitsToDouble(expected) == 20502050 && actual == expected
            && Sum(native, "stack_segment_allocations") > 1
            && Sum(native, "stack_segment_allocations") == Sum(control, "stack_segment_allocations")
            && Sum(native, "stack_frames") == Sum(control, "stack_frames")
            && Sum(native, "returns") == Sum(control, "returns")
            && Sum(native, "machine_calls") > 1000,
            "direct recursion restores segment limit and return target, preserves field identity and exact result");
    }

    private static void ImportFailure()
    {
        const string source = """
            fn leaf(input: Number) -> Number { $std::math::clamp(input, 1, -1) };
            fn middle(input: Number) -> Number { leaf(input) + 4 };
            middle(7)
            """;
        HpirProgram program = RoundTrip(Bind(Compile(source, "machine-import-failure.msync")));
        using var machine = HpirMachineImage.Compile(program);
        List<RuntimeTimelineRecord> events = [];
        try
        {
            _ = HpirExecutor.RunMachineCandidate(machine, new() { Timeline = new(events.Add, "machine-failure") });
            throw new Exception("Direct HPIR call discarded failure from typed host leaf");
        }
        catch (Msync.Core.MsyncException error) when (error.Message == "math.clamp requires lower <= upper.") { }
        var failure = events.Single(item => item.Name == "hpir.execute");
        Require(failure.Attributes["failure.function.name"] == "leaf"
            && failure.Attributes["source.file"] == "machine-import-failure.msync"
            && failure.Metrics["stack.frames_discarded"] == 3,
            "failure crosses direct frames with the precise host operation source and owning frame chain");
    }

    private static void Collection()
    {
        const string source = """
            fn make(seed: Number) {
                let n = seed;
                fn next(step: Number) -> Number { n = n + step; n };
                next
            };
            fn churn(limit: Number) -> Number {
                let i = 0;
                loop {
                    if i >= limit { break i; };
                    let temporary = make(i);
                    i = temporary(1);
                }
            };
            let first = make(8);
            let separate = make(20);
            let count = churn(20000);
            first(3) + first(4) + separate(1) + count
            """;
        HpirProgram program = RoundTrip(Bind(Compile(source, "machine-collection.msync")));
        var interpreted = new RuntimeProfile();
        ulong expected = HpirExecutor.Run(program, new() { Profile = interpreted });
        var profile = new RuntimeProfile();
        using var machine = HpirMachineImage.Compile(program, observe: true);
        ulong actual = HpirExecutor.RunMachineCandidate(machine, new() { Profile = profile });
        var native = profile.Drain();
        var control = interpreted.Drain();
        Require(BitConverter.UInt64BitsToDouble(expected) == 20047 && actual == expected
            && Sum(native, "gc_collections") > 0
            && Sum(native, "gc_collections") == Sum(control, "gc_collections")
            && Sum(native, "gc_stack_frames") == Sum(control, "gc_stack_frames")
            && Sum(native, "gc_root_locations") == Sum(control, "gc_root_locations")
            && Sum(native, "machine_calls") > 30000,
            "actual guest collection scans suspended native frames and retains escaping captures");
    }

    private static void PrimitiveBoundaries()
    {
        const ulong m = ulong.MaxValue, sign = 1UL << 63, nan = 0x7ff8000000000000;
        (HpirOp Operation, ulong Left, ulong Right, ulong Expected)[] cases =
        [
            (HpirOp.AddU64, m, 2, 1), (HpirOp.SubU64, 0, 1, m), (HpirOp.MulU64, m, 3, m - 2),
            (HpirOp.AndU64, m, sign, sign), (HpirOp.OrU64, sign, 1, sign + 1), (HpirOp.XorU64, m, sign, sign - 1),
            (HpirOp.ShlU64, 1, 65, 2), (HpirOp.ShrU64, sign, 65, sign >> 1), (HpirOp.SarI64, m - 1, 65, m),
            (HpirOp.LtU64, m, 1, 0), (HpirOp.LeU64, m, m, 1),
            (HpirOp.LtI64, m, 1, 1), (HpirOp.LeI64, 1, m, 0),
            (HpirOp.SignExtendI8, 0x80, 0, m - 0x7f), (HpirOp.SignExtendI16, 0x8000, 0, m - 0x7fff),
            (HpirOp.SignExtendI32, 0x80000000, 0, m - 0x7fffffff),
            (HpirOp.EqF64, nan, 0, 0), (HpirOp.NeF64, nan, 0, 1),
            (HpirOp.LtF64, 0, nan, 0), (HpirOp.LeF64, 0, nan, 0),
            (HpirOp.GtF64, 0, nan, 0), (HpirOp.GeF64, 0, nan, 0),
            (HpirOp.NegF64, 0, 0, sign), (HpirOp.RemF64, sign, 0x4008000000000000, sign)
        ];
        var code = new List<HpirInstruction>();
        foreach (var (index, test) in cases.Index())
        {
            code.Add(new(HpirOp.ConstU64, 0, Immediate: unchecked((long)test.Left)));
            code.Add(new(HpirOp.ConstU64, 8, Immediate: unchecked((long)test.Right)));
            code.Add(new(HpirOp.ConstU64, 16));
            code.Add(new(test.Operation, 16, 0, 8));
            code.Add(new(HpirOp.ConstU64, 24, Immediate: unchecked((long)test.Expected)));
            code.Add(new(HpirOp.EqU64, 32, 16, 24));
            int branch = code.Count;
            code.Add(new(HpirOp.Branch, branch + 1, 32, branch + 3));
            code.Add(new(HpirOp.ConstU64, 0, Immediate: index + 1));
            code.Add(new(HpirOp.Return, A: 0));
        }
        code.Add(new(HpirOp.ConstU64, 0));
        code.Add(new(HpirOp.Return, A: 0));
        HpirProgram program = RoundTrip(new([new("primitive-boundaries", 40, HpirWidth.U64, [], code.ToArray(), [], [])], 0));
        using var machine = HpirMachineImage.Compile(program);
        ulong expected = HpirExecutor.Run(program);
        ulong actual = HpirExecutor.RunMachineCandidate(machine);
        Require(expected == 0 && actual == 0, $"primitive boundary mismatch: interpreted case {expected}, native case {actual}");
    }

    private static void ForwardedScalars()
    {
        static HpirInstruction Number(int offset, double value) =>
            new(HpirOp.ConstU64, D: offset, Immediate: BitConverter.DoubleToInt64Bits(value));

        foreach (bool skip in new[] { false, true })
        {
            HpirProgram program = RoundTrip(new([new("forwarded-scalars", 40, HpirWidth.U64, [], [
                Number(0, 6), Number(8, 2), Number(16, 9),
                new(HpirOp.AddressOf, D: 24, A: 16),
                new(HpirOp.ConstU8, D: 32, Immediate: skip ? 0 : 1),
                new(HpirOp.Branch, D: 7, A: 32, B: 6),
                new(HpirOp.AddF64, D: 16, A: 0, B: 8),
                new(HpirOp.SubF64, D: 16, A: 16, B: 8),
                new(HpirOp.MulF64, D: 16, A: 16, B: 16),
                new(HpirOp.DivF64, D: 0, A: 16, B: ~24),
                new(HpirOp.AddF64, D: 0, A: 0, B: 16),
                new(HpirOp.Return, A: 0)
            ], [], [])], 0));
            List<RuntimeTimelineRecord> events = [];
            using var machine = HpirMachineImage.Compile(program, timeline: new(events.Add, "scalar-runs"));
            ulong oracle = BitConverter.DoubleToUInt64Bits(skip ? 50 : 37);
            Require(HpirExecutor.Run(program) == oracle && HpirExecutor.RunMachineCandidate(machine) == oracle,
                "scalar runs respect an alternate entry and an indirect operand aliasing the running result");
            RuntimeTimelineRecord emit = events.Single(item => item.Name == "hpir.machine.emit.function");
            Require(emit.Metrics["scalar.forwarded_operands"] == 3 && emit.Metrics["scalar.elided_stores"] == 2,
                "carry scalar results in registers but publish the write observed through the pointer");
        }

        HpirProgram words = RoundTrip(new([new("forwarded-words", 16, HpirWidth.U64, [], [
            new(HpirOp.ConstU64, D: 0, Immediate: 2),
            new(HpirOp.ConstU64, D: 8, Immediate: 4),
            new(HpirOp.AddU64, D: 0, A: 0, B: 8),
            new(HpirOp.MulU64, D: 0, A: 0, B: 0),
            new(HpirOp.XorU64, D: 0, A: 0, B: 8),
            new(HpirOp.Return, A: 0)
        ], [], [])], 0));
        using var wordMachine = HpirMachineImage.Compile(words);
        Require(HpirExecutor.Run(words) == 32 && HpirExecutor.RunMachineCandidate(wordMachine) == 32,
            "word runs forward through the two-byte multiply opcode and an overlapping destination");
    }

    private static void Cancellation()
    {
        const string source = """
            fn spin() {
                $std::io::println("ready");
                loop { }
            };
            spin()
            """;
        HpirProgram program = RoundTrip(Bind(Compile(source, "machine-cancellation.msync")));
        using var machine = HpirMachineImage.Compile(program);
        using var stop = new CancellationTokenSource();
        using var writer = new MarkerWriter();
        List<RuntimeTimelineRecord> timeline = [];
        Task execution = Task.Run(() => HpirExecutor.RunMachineCandidate(machine, new(Output: writer)
        {
            Timeline = new(timeline.Add, "hpir-machine-cancel")
        }, stop.Token));
        try
        {
            Require(writer.Started.Wait(TimeSpan.FromSeconds(4)), "actual effect before cancellation");
        }
        finally
        {
            stop.Cancel();
        }
        try
        {
            execution.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            throw new Exception("HPIR machine loop ignored cancellation");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { }
        RuntimeTimelineRecord failed = timeline.Single(item => item.Name == "hpir.execute");
        int pc = checked((int)failed.Metrics["failure.instruction"]);
        int function = int.Parse(failed.Attributes["failure.function.id"], System.Globalization.CultureInfo.InvariantCulture);
        Require(writer.ToString() == "ready\n" && failed.Outcome == "cancelled"
            && failed.Attributes["source.file"] == "machine-cancellation.msync"
            && failed.Attributes["failure.function.name"] == "spin"
            && program.Functions[function].Instructions[pc].Op is HpirOp.Jump or HpirOp.Branch,
            "no repeated effect; cancel at original guest backedge");
    }

    private static IEnumerable<string> Flow(IEnumerable<RuntimeProfileSample> profile, IReadOnlyList<string> kinds) =>
        profile.Where(item => kinds.Contains(item.Name)).Select(item => item.Name
            + ":" + string.Join(";", item.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            + ":" + string.Join(";", item.Metrics.Where(pair => pair.Key != "machine_inline_calls")
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))).Order(StringComparer.Ordinal);

    private static HpirProgram Compile(string source, string name) =>
        Compiler.LowerHpir(Compiler.Analyze(source, name), 2);

    private static HpirProgram Bind(HpirProgram program) =>
        HpirBinding.Prepare(program, new(Environment.CurrentDirectory, Environment.CurrentDirectory));

    private static HpirProgram Load(string path)
    {
        using var image = File.OpenRead(path);
        return HpirArtifact.ReadTrusted(image);
    }

    private static void Save(HpirProgram program, string path)
    {
        using var image = File.Create(path);
        HpirArtifact.WriteProduced(program, image);
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("HPIR machine: " + message);
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal ManualResetEventSlim Started { get; } = new();

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            Write('\n');
            Started.Set();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Started.Dispose();
            base.Dispose(disposing);
        }
    }
}
