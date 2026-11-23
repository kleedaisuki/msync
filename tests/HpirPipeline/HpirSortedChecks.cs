using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirSortedChecks
{
    private const string Source = """
        let ranges = $std::ranges;
        let join = $std::string::join;
        let print = $std::io::println;
        fn show(values: Range) {
            let labels = ranges::map(values, fn(value) { value as String });
            print(join(labels, ","));
        };
        show(ranges::merge([1, 1], [1, 2]));
        show(ranges::merge([3], [2, 4]));
        show(ranges::set_union([1, 1], [1, 2]));
        let nan = 0 / 0;
        show(ranges::merge([nan, 1], [nan, 0]));
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            List<CompilationObservation> observations = [];
            var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
            var module = Compiler.Analyze(Source, "sorted-cursors.msync", trace);
            var portable = RoundTrip(Compiler.LowerHpir(module, level, trace));
            var executable = RoundTrip(HpirBinding.Prepare(portable,
                new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
            string[] bodies = portable.Functions.Select(function => function.Name).Where(name =>
                name == "show" || name.StartsWith("show.", StringComparison.Ordinal)).ToArray();
            bool keyed = observations.Any(item => item.Metrics?.GetValueOrDefault("key.sorted_range_recipes") > 0);
            int pulls = observations.Count(item => item.Metrics?.GetValueOrDefault("range.sorted.compiled_pulls") > 0);
            if (bodies.Length != 2 || !keyed || pulls != 2)
                throw new Exception($"Expected two sorted recipe bodies O{level}; got [{string.Join(", ", bodies)}], keys={keyed}, pulls={pulls}");
            Check(executable, native: false);
            if (HpirMachineImage.IsSupported) Check(executable, native: true);
        }
        Console.WriteLine("PASS HPIR sorted recipe reuse, multisets and natural NaN ordering");
    }

    private static void Check(HpirProgram executable, bool native)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var options = new ExecutionOptions(Output: output);
        using var code = native ? HpirMachineImage.Compile(executable) : null;
        if (code is null) HpirExecutor.Run(executable, options);
        else HpirExecutor.RunMachineCandidate(code, options);
        if (output.ToString() != "1,1,1,2\n2,3,4\n1,1,2\nNaN,NaN,0,1\n")
            throw new Exception($"Sorted typed cursors changed the language ordering: {output}");
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
