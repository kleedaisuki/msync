using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirVectorAccessChecks
{
    private const string Source = """
        fn ordinal() -> Number {
            let line? = $std::io::read_line();
            if line is null { return 0; };
            line as Number
        };
        let vector = [7, 11, 13];
        let first = $std::ranges::front(vector);
        let size = $std::ranges::size(vector);
        let index = ordinal();
        $std::ranges::at(vector, index) + first * 10 + size * 100
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            List<CompilationObservation> observations = [];
            var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
            var module = Compiler.Analyze(Source, "vector-lookup.msync", trace);
            HpirProgram program = Compiler.LowerHpir(module, level, trace);
            using var stream = new MemoryStream();
            HpirArtifact.WriteProduced(program, stream);
            stream.Position = 0;
            program = HpirBinding.Prepare(HpirArtifact.ReadTrusted(stream), new(Environment.CurrentDirectory, Environment.CurrentDirectory));
            Require(program.Functions.SelectMany(function => function.Instructions).Any(op => op.Op == HpirOp.OrdinalF64ToU64)
                && program.Functions.SelectMany(function => function.Instructions).Any(op => op.Op == HpirOp.ConvertI64ToF64)
                && observations.Any(item => item.Metrics?.GetValueOrDefault("vectors.at") == 1
                    && item.Metrics.GetValueOrDefault("vectors.front") == 1
                    && item.Metrics.GetValueOrDefault("vectors.size") == 1), "The producer did not select the physical vector.");
            Check(program, null);
            if (HpirMachineImage.IsSupported)
            {
                using var image = HpirMachineImage.Compile(program);
                Check(program, image);
            }
            ReadyFirst(level);
        }
        Console.WriteLine("PASS ready vector at/front/first/size, native index conversion and precise cold errors at O0–O3");
    }

    private static void ReadyFirst(int level)
    {
        const string source = """
            let calls = 0;
            fn delayed_values() {
                calls = calls + 1;
                [calls]
            };
            let values = delayed_values();
            let before = calls;
            let first = $std::ranges::first(values);
            let second = $std::ranges::first(values);
            let member? = $std::ranges::first([null, 5]);
            if member is null { before * 1000 + first * 100 + second * 10 + calls } else { -1 }
            """;
        var observations = new List<CompilationObservation>();
        string cwd = Environment.CurrentDirectory;
        const string name = "vector-first.msync";
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram Prepare(string body, CompilationTrace? probe = null) => HpirBinding.Prepare(
            Compiler.LowerHpir(Compiler.Analyze(body, name, probe), level, probe), new(cwd, cwd));
        HpirProgram program = Prepare(source, trace);
        Require(observations.Any(item => item.Metrics?.GetValueOrDefault("vectors.first") == 3),
            "Direct wrapped snapshots did not select all three first reads.");
        EachEngine(program, run => Require(BitConverter.UInt64BitsToDouble(run()) == 1111,
            "First did not preserve the ready snapshot's once-evaluated producer and in-range Null."));

        foreach (string empty in new[] { "empty_of(Number)", "drop([5], 1)" })
        {
            string range = "$std::ranges::" + empty;
            EachEngine(Prepare($"$std::ranges::first({range});"), run =>
            {
                try { _ = run(); }
                catch (MsyncException error) when (error.Span?.Source == name
                    && error.Message.Contains("ranges.first found no matching item", StringComparison.Ordinal))
                {
                    return;
                }
                throw new InvalidOperationException("An empty snapshot did not retain the first-specific error and source.");
            });
        }
    }

    private static void EachEngine(HpirProgram program, Action<Func<ulong>> check)
    {
        check(() => HpirExecutor.Run(program));
        if (!HpirMachineImage.IsSupported) return;
        using var image = HpirMachineImage.Compile(program);
        check(() => HpirExecutor.RunMachineCandidate(image));
    }

    private static void Check(HpirProgram program, HpirMachineImage? machine)
    {
        Run("-0", 377);
        Run("1", 381);
        Run("2", 383);
        Run("3", error: "ranges.at has no item at index 3");
        Run("9223372036854774784", error: "ranges.at has no item at index 9223372036854774784");
        const string invalid = "ranges.at requires a nonnegative integral count representable by the platform iterator";
        Run("-1", error: invalid);
        Run("1.25", error: invalid);
        Run("9223372036854775808", error: invalid);
        Run("NaN", error: "ranges.at requires finite Number arguments");
        Run("Infinity", error: "ranges.at requires finite Number arguments");
        return;

        void Run(string input, double expected = 0, string? error = null)
        {
            using var reader = new StringReader(input + "\n");
            var options = new ExecutionOptions { Input = reader };
            try
            {
                ulong bits = machine is null ? HpirExecutor.Run(program, options) : HpirExecutor.RunMachineCandidate(machine, options);
                Require(error is null && BitConverter.UInt64BitsToDouble(bits) == expected,
                    $"Vector access produced the wrong value for index {input}.");
            }
            catch (MsyncException failure) when (error is not null)
            {
                Require(failure.Message == error
                    && failure.Span?.Start == Source.IndexOf("$std::ranges::at", StringComparison.Ordinal),
                    $"Vector access lost index failure detail or source for {input}.");
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
