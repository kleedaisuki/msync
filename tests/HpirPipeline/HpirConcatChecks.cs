using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirConcatChecks
{
    private const string Source = """
        let numbers = $std::ranges::empty_of(Number);
        let before = $std::ranges::materialize(numbers);
        numbers = $std::ranges::materialize($std::ranges::concat(numbers, [7]));
        $std::io::println($std::ranges::size(before) as String);
        $std::io::println($std::ranges::at(numbers, 0) as String);

        let text = $std::ranges::concat(["α"], ["β", "🙂"]);
        $std::io::println($std::ranges::size(text) as String);
        $std::io::println($std::ranges::at(text, 1));
        $std::io::println($std::string::join($std::ranges::materialize(text), ","));

        fn plan(left: Object) {
            let right = object { label: "right" };
            let skipped = object { label: "unreachable" };
            $std::ranges::concat([left], $std::ranges::drop([skipped, right], 1))
        };
        let left = object { label: "left" };
        let parts = plan(left);
        let garbage = $std::ranges::materialize(
            $std::ranges::transform($std::ranges::iota(0, 8192), fn(value: Number) { object { value: value } })
        );
        $std::io::println($std::ranges::size(garbage) as String);
        $std::io::println($std::ranges::front(parts)::label);
        let packed = $std::ranges::materialize(parts);
        let first = $std::ranges::at(packed, 0);
        let second = $std::ranges::at(packed, 1);
        first::label = "shared";
        second::label = "retained";
        $std::io::println(left::label);
        for item in parts { $std::io::println(item::label); };
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            List<CompilationObservation> observations = [];
            var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
            var module = Compiler.Analyze(Source, "concat-snapshots.msync", trace);
            var portable = RoundTrip(Compiler.LowerHpir(module, level, trace));
            var program = RoundTrip(HpirBinding.Prepare(portable, new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
            Require(observations.Any(row => row.Metrics?.GetValueOrDefault("range.concat.empty_aliases") > 0)
                && observations.Any(row => row.Metrics?.GetValueOrDefault("range.concat.segment_plans") > 0)
                && observations.Any(row => row.Metrics?.GetValueOrDefault("range.concat.pull_sites") > 0),
                "Empty merge, retained segment or physical cursor was not selected.");
            Check(program, native: false);
            if (HpirMachineImage.IsSupported) Check(program, native: true);
        }
        Console.WriteLine("PASS HPIR concat: no-copy empty and segment plans, direct pulls, bulk pack, and aliased Object lifetime");
    }

    private static void Check(HpirProgram program, bool native)
    {
        var profile = new RuntimeProfile(instructionSites: true);
        using var output = new StringWriter { NewLine = "\n" };
        var options = new ExecutionOptions(Output: output) { Profile = profile };
        using var code = native ? HpirMachineImage.Compile(program, observe: true) : null;
        if (code is null) HpirExecutor.Run(program, options);
        else HpirExecutor.RunMachineCandidate(code, options);
        Require(output.ToString() == "0\n7\n3\nβ\nα,β,🙂\n8192\nleft\nshared\nshared\nretained\n",
            "Concat changed the source order, Unicode data, or record aliases.");
        var rows = profile.Drain().Where(row => row.Name == "vm.function").ToArray();
        ulong Sum(string key) => rows.Aggregate(0UL, (total, row) => total + row.Metrics.GetValueOrDefault(key));
        Require(Sum("gc_collections") > 0 && Sum("gc_borrow_matches") > 0,
            "Concat did not exercise collection with an interior backing pointer.");
        Require(Sum("range_packs") == 2 && Sum("range_packed_words") == 5,
            "Concat copied elements outside the two explicitly requested materializations.");
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
