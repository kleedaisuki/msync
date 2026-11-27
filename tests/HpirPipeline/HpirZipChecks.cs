using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirZipChecks
{
    private const int JoinCount = 4096;

    private const string Order = """
        let trace = 0;
        fn left(n: Number) -> Number { global trace; trace = trace * 10 + 1; n };
        fn right(n: Number) -> Number { global trace; trace = trace * 10 + 2; n };
        let a = $std::ranges::transform([1, 3, 5], left);
        let b = $std::ranges::transform([2, 4], right);
        let z = $std::ranges::zip(a, b);
        let zero = $std::ranges::take(z, 0);
        for pair in zero { $std::io::println("unexpected"); };
        $std::io::println($std::ranges::size(zero) as String);
        $std::io::println(trace as String);
        let total = 0;
        for pair in z { total = total + pair::first + pair::second; };
        $std::io::println(total as String);
        $std::io::println(trace as String);
        trace = 0;
        for pair in $std::ranges::zip(b, a) { };
        $std::io::println(trace as String);
        trace = 0;
        for pair in $std::ranges::take(z, 1) { };
        $std::io::println(trace as String);
        """;

    private const string Text = """
        let pairs = $std::ranges::zip(["a", "b"], ["1", "2"]);
        let labels = $std::ranges::transform(pairs, fn(pair) {
            $std::string::concat(pair::first, pair::second)
        });
        for label in labels { $std::io::println(label); };
        """;

    private const string Alias = """
        let original = object { value: 2 };
        let other = object { value: 5 };
        let pair = $std::ranges::front($std::ranges::zip([original], [other]));
        pair::first::value = 19;
        $std::io::println(original::value as String);
        $std::io::println(pair::second::value as String);
        """;

    private const string Join = """
        $std::io::println($std::string::join(["", "α", "β"], "🙂"));
        $std::io::println($std::string::join(["α"], "🙂"));
        $std::io::println($std::string::join($std::ranges::empty_of(String), "🙂"));
        let pulls = 0;
        fn label(n: Number) -> String {
            global pulls;
            pulls = pulls + 1;
            $std::string::concat("α", n as String)
        };
        let labels = $std::ranges::transform($std::ranges::iota(0, 4096), label);
        $std::io::println($std::string::join(labels, ","));
        $std::io::println(pulls as String);
        """;

    internal static void Run()
    {
        string joined = string.Join(',', Enumerable.Range(0, JoinCount).Select(n => "α" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        for (int level = 0; level <= 3; ++level)
        {
            Check(Order, "zip-order", "0\n0\n10\n12121\n2121\n12\n", level);
            Check(Text, "zip-text-map", "a1\nb2\n", level);
            Check(Alias, "zip-alias", "19\n5\n", level);
            Check(Join, "text-join", $"🙂α🙂β\nα\n\n{joined}\n{JoinCount}\n", level, joinUnits: joined.Length + 7);
        }
        Console.WriteLine("PASS HPIR typed zip and Text join: asymmetric demand, aliases, Unicode and one callback traversal across collection");
    }

    private static void Check(string source, string name, string expected, int level, int joinUnits = -1)
    {
        List<CompilationObservation> observations = [];
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram compiled = Compiler.LowerHpir(Compiler.Analyze(source, name + ".msync", trace), level, trace);
        HpirProgram executable = RoundTrip(HpirBinding.Prepare(RoundTrip(compiled),
            new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
        if (joinUnits < 0)
            Require(observations.Any(item => item.Metrics?.GetValueOrDefault("range.zip.frame_cursors") > 0),
                name + ": no concrete pair cursor was emitted");
        else
            Require(observations.Any(item => item.Metrics?.GetValueOrDefault("strings.join.snapshots") == 3)
                && observations.Any(item => item.Metrics?.GetValueOrDefault("strings.join.compiled_traversals") == 1),
                name + ": snapshot and actual callback traversal were not selected");
        Run(false);
        if (HpirMachineImage.IsSupported) Run(true);

        void Run(bool native)
        {
            using var output = new StringWriter { NewLine = "\n" };
            var profile = joinUnits >= 0 ? new RuntimeProfile(instructionSites: true) : null;
            var options = new ExecutionOptions(Output: output) { Profile = profile };
            if (!native) HpirExecutor.Run(executable, options);
            else
            {
                using var machine = HpirMachineImage.Compile(executable, observe: profile is not null);
                HpirExecutor.RunMachineCandidate(machine, options);
            }
            Require(output.ToString() == expected, $"{name} O{level}: {output}");
            if (profile is not null)
            {
                var samples = profile.Drain();
                ulong Sum(string key) => samples.Where(row => row.Name == "vm.function")
                    .Aggregate(0UL, (count, row) => count + row.Metrics.GetValueOrDefault(key));
                Require(Sum("gc_collections") > 0 && Sum("text_join_items") == JoinCount + 4
                    && Sum("text_join_code_units") == (ulong)joinUnits, "actual Text packing or snapshot collection census disagrees");
            }
        }
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
