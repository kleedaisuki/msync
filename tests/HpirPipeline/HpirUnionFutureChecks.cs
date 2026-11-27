using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirUnionFutureChecks
{
    private const string Name = "union-completion.msync";
    private const string Source = """
        fn choose(select: Bool, value: Number) -> Number? {
            if select { value } else { null }
        };
        fn relay(value: Number?) -> Number? { value };
        fn sample() {
            object { item?: relay(choose(exists ./select, $std::io::read_text(./seed.txt) as Number)) }
        };
        let kept = sample();
        let churn = 0;
        loop {
            if churn >= 14000 { break; };
            churn = ((churn as String) as Number) + 1;
        };
        let observed? = kept::item;
        if observed is null { "absent" -> ./before.txt; };
        if observed is not null and observed == 7 { "seven" -> ./before.txt; };
        let alias = kept;
        alias::item = 13;
        let restored? = kept::item;
        if restored is not null { (restored as String) -> ./after.txt; };
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", $"union-completion-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                List<CompilationObservation> observations = [];
                var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
                var portable = Reload(Compiler.LowerHpir(Compiler.Analyze(Source, Name, trace), level, trace));
                var program = Reload(HpirBinding.Prepare(portable, new(root, root)));
                if (!observations.Any(item => item.Metrics?.GetValueOrDefault("future.union_cells") > 0))
                    throw new InvalidOperationException($"O{level} nullable Number lost its completion lane.");
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : new[] { false })
                {
                    Check(program, root, native, selected: false, seed: "trapped", expected: "absent");
                    Check(program, root, native, selected: true, seed: "7", expected: "seven");
                    Check(program, root, native, selected: true, seed: "trapped", expected: null);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS nullable Number retains the selected completion across guest calls, a heap field, alias writes and GC at O0–O3");
    }

    private static void Check(HpirProgram program, string root, bool native, bool selected, string seed, string? expected)
    {
        string before = Path.Combine(root, "before.txt");
        string after = Path.Combine(root, "after.txt");
        string flag = Path.Combine(root, "select");
        File.Delete(before);
        File.Delete(after);
        if (selected) File.WriteAllText(flag, ""); else File.Delete(flag);
        File.WriteAllText(Path.Combine(root, "seed.txt"), seed);
        var profile = new RuntimeProfile();
        var options = new ExecutionOptions(WorkingDirectory: root) { Profile = profile };
        using var image = native ? HpirMachineImage.Compile(program, observe: true) : null;
        try
        {
            HpirRunOutcome result = image is not null
                ? HpirExecutor.RunMachineOutcome(image, options) : HpirExecutor.RunOutcome(program, options);
            if (expected is null || result.FailedTransfers != 0 || File.ReadAllText(before) != expected
                || File.ReadAllText(after) != "13")
                throw new InvalidOperationException("The selected union arm, alias mutation, or completion result changed.");
        }
        catch (MsyncException error) when (expected is null && error.Span?.Source == Name
            && error.Message.Contains("Cannot cast String 'trapped' to Number", StringComparison.Ordinal))
        {
            if (File.Exists(before) || File.Exists(after))
                throw new InvalidOperationException("Failed Number observation published a guarded effect.");
        }
        if (!profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") != 0))
            throw new InvalidOperationException("The retained union was not tested across actual heap collection.");
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
