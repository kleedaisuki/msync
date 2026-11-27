using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirCountedRangeChecks
{
    private const string Wrapped = """
        fn empty_values(seed: Type) { $std::ranges::empty_of(seed) };
        fn index_from(input: Range) { $std::ranges::enumerate_from(input, -1.5) };
        let view = index_from($std::ranges::repeat(20, 3));
        let total = 0;
        for item in view { total = total + item::index + item::value; };
        $std::io::println(total as String);
        $std::io::println($std::ranges::size(empty_values(Number)) as String);
        if $std::ranges::empty(empty_values(Number)) { $std::io::println("empty"); };
        let wide = $std::ranges::repeat(7, 9223372036854774784);
        if not($std::ranges::empty(wide)) { $std::io::println($std::ranges::front(wide) as String); };
        for item in wide { $std::io::println(item as String); break; };
        """;

    private const string Generate = """
        let calls = 0;
        fn make(index: Number) -> Number { global calls; calls = calls + 1; index * 3 + 2 };
        fn factory(count: Number) { $std::ranges::generate(count, make) };
        fn third(values: Range) { $std::ranges::at(values, 2) };
        let lazy = factory(4);
        $std::io::println(calls as String);
        let result = $std::ranges::materialize(lazy);
        $std::io::println($std::ranges::size(result) as String);
        $std::io::println(third(result) as String);
        $std::io::println(calls as String);
        """;

    private const string Aliased = """
        let cell = object { hits: 0 };
        let rows = $std::ranges::enumerate($std::ranges::repeat(cell, 2));
        for row in rows { row::value::hits = row::value::hits + 1; };
        $std::io::println(cell::hits as String);
        """;

    private const string Stalled = """
        let rows = $std::ranges::enumerate_from($std::ranges::repeat(null, 1), 9007199254740992);
        for row in rows { $std::io::println(row::index as String); };
        """;

    private const string SnapshotWindow = """
        fn owned() {
            let discarded = object { n: -1 };
            let first = object { n: 17 };
            let next = object { n: 25 };
            $std::ranges::drop([discarded, first, next], 1)
        };
        fn churn(limit: Number) -> Number {
            let index = 0;
            loop {
                if index >= limit { break index; };
                let scratch = object { n: index };
                index = scratch::n + 1;
            }
        };
        let view = $std::ranges::materialize($std::ranges::take(owned(), 2));
        let total = 0;
        for item in view {
            let cells = churn(12000);
            total = total + item::n + (cells - 12000);
        };
        $std::io::println(total as String);
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            Check("wrapped-counted", Wrapped, level, "58.5\n0\nempty\n7\n7\n", counted: true);
            Check("stdlib-generate", Generate, level, "0\n4\n8\n4\n", counted: true);
            Check("repeat-alias", Aliased, level, "2\n", counted: true);
        }
        Check("indexed-stall-break", Stalled.Replace("row::index as String);", "row::index as String); break;", StringComparison.Ordinal),
            2, "9007199254740992\n", counted: true);
        Check("snapshot-window", SnapshotWindow, 2, "42\n", counted: false, collects: true);
        Fails("indexed-stall-demand", Stalled, "ranges.enumerate_from index no longer advances", "9007199254740992\n");
        Fails("repeat-invalid-count", "let rejected = $std::ranges::repeat(1, 9223372036854775808);",
            "ranges.repeat requires a nonnegative integral count", "");
        Fails("empty-first", "$std::ranges::front($std::ranges::empty_of(Number))",
            "ranges.front requires a nonempty Range", "");
        CheckStoredEmpty();
        Console.WriteLine("PASS HPIR typed empty/repeat/enumerate and source stdlib generate through fixed wrappers");
    }

    private static void Check(string name, string source, int level, string expected, bool counted, bool collects = false)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram image = Prepare(name, source, level, trace);
        Require(!counted || observations.Any(record => record.Metrics?.GetValueOrDefault("range.counted_pull_sites") > 0),
            name + ": missing actual counted cursor selection");
        Run(false);
        if (HpirMachineImage.IsSupported) Run(true);

        void Run(bool native)
        {
            using var output = new StringWriter { NewLine = "\n" };
            var profile = collects ? new RuntimeProfile() : null;
            var options = new ExecutionOptions(Output: output) { Profile = profile };
            if (!native) HpirExecutor.Run(image, options);
            else
            {
                using var machine = HpirMachineImage.Compile(image, observe: collects);
                HpirExecutor.RunMachineCandidate(machine, options);
            }
            Require(output.ToString() == expected, $"{name} O{level}: expected {expected}, got {output}");
            Require(profile is null || profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_borrow_matches") > 0),
                name + ": the test did not observe the backing allocation through the interior slice");
        }
    }

    private static void Fails(string name, string source, string reason, string expectedPrefix)
    {
        HpirProgram image = Prepare(name, source, 2);
        using var output = new StringWriter { NewLine = "\n" };
        try { HpirExecutor.Run(image, new(Output: output)); }
        catch (MsyncException error) when (error.Message.Contains(reason, StringComparison.Ordinal)
            && error.Span?.Source == name + ".msync" && output.ToString() == expectedPrefix)
        {
            return;
        }
        throw new Exception(name + ": wrong demand, failure origin, or error text");
    }

    private static void CheckStoredEmpty()
    {
        const string empty = """
            fn first(pick: Bool) {
                let holder = if pick { object { seed: $std::ranges::empty_of(Number) } }
                    else { object { marker: 1, seed: $std::ranges::take([8], 0) } };
                $std::ranges::front(holder::seed)
            };
            first(false)
            """;
        Fails("empty-field-projection", empty, "ranges.front requires a nonempty Range", "");
        const string observe = """
            fn choose(pick: Bool) {
                if pick { object { seed: $std::ranges::empty_of(Number) } }
                else { object { marker: 1, seed: $std::ranges::take([8], 0) } }
            };
            let holder = choose(false);
            $std::io::println($std::ranges::size(holder::seed) as String);
            if $std::ranges::empty(holder::seed) { $std::io::println("empty"); };
            let mixed = if $std::ranges::empty(holder::seed) { [] } else { $std::ranges::empty_of(Number) };
            for item in mixed { $std::io::println("unexpected"); };
            """;
        Check("empty-field-size-and-iteration", observe, 2, "0\nempty\n", counted: false);

        const string changed = """
            let holder = object { seed: $std::ranges::empty_of(Number) };
            fn replace() { holder::seed = [7]; };
            replace();
            $std::ranges::front(holder::seed)
            """;
        List<CompilationObservation> observations = [];
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        _ = LlvmHirLowerer.Lower(Compiler.Analyze(changed, "mutable-range-field.msync", trace), trace);
        Require(observations.Any(item => item.Metrics?.GetValueOrDefault("range.fields.writes") > 0)
            && observations.All(item => item.Metrics?.GetValueOrDefault("range.fields.empty_consumers") is not > 0),
            "An actual nonempty store must prevent the earlier empty initializer from specializing the field's front.");
    }

    private static HpirProgram Prepare(string name, string source, int level, CompilationTrace? trace = null)
    {
        var compiled = Compiler.LowerHpir(Compiler.Analyze(source, name + ".msync", trace), level, trace);
        var detached = RoundTrip(compiled);
        return RoundTrip(HpirBinding.Prepare(detached, new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
