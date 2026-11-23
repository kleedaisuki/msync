using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirGlobalChecks
{
    private const string Source = """
        scope Cache { item: scope { value: 1 } };
        fn next(seed: Number) -> Number {
            scope Counter { n: seed };
            Counter::n = Counter::n + 1;
            Counter::n
        };
        fn churn(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                let cell = scope { value: i };
                i = cell::value + 1;
            }
        };
        Cache::item = scope { value: 37 };
        let one = next(10);
        let two = next(89);
        let count = churn(20000);
        one * 10000 + two * 100 + Cache::item::value + (count - 20000)
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            CheckSharedGeometry(level);
            HpirProgram portable = Roundtrip(Compiler.LowerHpir(Compiler.Analyze(Source, "singletons.msync"), level));
            string directory = Environment.CurrentDirectory;
            HpirProgram bound = Roundtrip(HpirBinding.Prepare(portable, new(directory, directory)));
            if (bound.Globals.Count != 2) throw new InvalidOperationException("Two singleton declarations need two locations, not instances per call.");
            using var machine = HpirMachineImage.IsSupported ? HpirMachineImage.Compile(bound) : null;
            Check(bound, null, level);
            if (machine is not null) Check(bound, machine, level);
            Check(bound, null, level);
        }
        Console.WriteLine("PASS HPIR named singleton identity, exact execution roots through collection, and fresh image execution state, O0–O3");
    }

    private static void CheckSharedGeometry(int level)
    {
        const string source = """
            let record = scope { value: "object" };
            fn make(message: String) { fn { message } };
            let action = make("closure");
            $std::io::println(record::value);
            $std::io::println(action());
            """;
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram portable = Compiler.LowerHpir(Compiler.Analyze(source, "shared-layout.msync"), level, trace);
        if (!observations.Any(item => item.Metrics?.GetValueOrDefault("layout.reused_structs") > 0))
            throw new InvalidOperationException("An equal Scope and capture-cell geometry should reuse one concrete LLVM layout.");
        string directory = Environment.CurrentDirectory;
        HpirProgram bound = Roundtrip(HpirBinding.Prepare(Roundtrip(portable), new(directory, directory)));
        using var output = new StringWriter { NewLine = "\n" };
        var options = new ExecutionOptions(Output: output);
        _ = HpirExecutor.Run(bound, options);
        if (output.ToString() != "object\nclosure\n")
            throw new InvalidOperationException("Interning a layout must keep independent Scope and closure identities.");
        if (!HpirMachineImage.IsSupported) return;
        output.GetStringBuilder().Clear();
        using var machine = HpirMachineImage.Compile(bound);
        _ = HpirExecutor.RunMachineCandidate(machine, options);
        if (output.ToString() != "object\nclosure\n")
            throw new InvalidOperationException("Emitted code must also preserve distinct allocations with the same layout.");
    }

    private static void Check(HpirProgram bound, HpirMachineImage? machine, int level)
    {
        List<RuntimeTimelineRecord> events = [];
        var options = new ExecutionOptions { Timeline = new(events.Add, "globals") };
        ulong result = machine is null ? HpirExecutor.Run(bound, options) : HpirExecutor.RunMachineCandidate(machine, options);
        if (BitConverter.UInt64BitsToDouble(result) != 111237
            || !events.Any(item => item.Name == "hpir.link.globals" && item.Metrics.GetValueOrDefault("globals.linked") == 2)
            || !events.Any(item => item.Name == "hpir.heap.collect" && item.Metrics.GetValueOrDefault("roots.globals") == 2))
            throw new InvalidOperationException($"Missing singleton state or reachability at O{level}: {BitConverter.UInt64BitsToDouble(result)}.");
    }

    private static HpirProgram Roundtrip(HpirProgram program)
    {
        using MemoryStream bytes = new();
        HpirArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }
}
