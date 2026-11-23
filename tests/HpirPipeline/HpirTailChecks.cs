using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTailChecks
{
    internal const string Deep = "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; $std::io::println(sum(100000, 0) as String);";
    private const string Swap = """
        fn swap(n, left, right) { if n == 0 { left } else { swap(n - 1, right, left) } };
        $std::io::println(swap(11, "left", "right"));
        """;
    private const string Capture = """
        let count = 0;
        fn walk(n) { count = count + 1; if n == 0 { count } else { walk(n - 1) } };
        $std::io::println(walk(20) as String);
        """;
    private const string Fresh = """
        let first = fn { 0 };
        fn walk(n) {
            let snapshot = n;
            if n == 3 { first = fn { snapshot }; };
            if n == 0 { null } else { walk(n - 1) }
        };
        walk(3);
        $std::io::println(first() as String);
        """;
    private const string Environments = """
        fn factory(value) { fn read(n) { value + n }; read };
        let first = factory(10);
        let second = factory(20);
        fn invoke(callable, n) { callable(n) };
        $std::io::println((invoke(first, 1) + invoke(second, 1)) as String);
        """;
    private const string Effects = """
        fn write(n) {
            if n == 2 { "first" -> ./first; };
            if n == 99 { "never" -> ./never; };
            if n == 0 { "last" -> ./last; true } else { write(n - 1) }
        };
        if write(2) { $std::io::println("done"); };
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "tail", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
                {
                    Check(Deep, root, level, native, "5000050000\n", loops: true, deep: true);
                    Check(Swap, root, level, native, "right\n", loops: true, moves: true);
                    Check(Capture, root, level, native, "21\n", loops: true);
                    Check(Fresh, root, level, native, "3\n", loops: true);
                    Check(Environments, root, level, native, "32\n", loops: false);
                    Check(Effects, root, level, native, "done\n", loops: false);
                    Require(File.ReadAllText(Path.Combine(root, "first")) == "first"
                        && File.ReadAllText(Path.Combine(root, "last")) == "last"
                        && !File.Exists(Path.Combine(root, "never")), "owned effects and their branch selection did not survive recursion");
                    File.Delete(Path.Combine(root, "first"));
                    File.Delete(Path.Combine(root, "last"));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR 100000-step tail loop, parallel argument exchange, captures, fresh escapes and selected effects");
    }

    private static void Check(string source, string root, int level, bool native, string expected, bool loops,
        bool deep = false, bool moves = false)
    {
        var compiler = new List<CompilationObservation>();
        var trace = new CompilationTrace(compiler.Add, includeOptimizerPasses: true);
        string file = Path.Combine(root, "source.msync");
        File.WriteAllText(file, source);
        HpirProgram portable = Cycle(Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(file), file), level, trace));
        File.Delete(file);
        HpirProgram image = Cycle(HpirBinding.Prepare(portable, new(root, root)));
        bool selected = compiler.Any(row => row.Metrics?.GetValueOrDefault("tail.self.loop_sites") > 0);
        Require(selected == loops, $"unexpected tail selection O{level} for {source}; expected {loops}");
        if (moves) Require(compiler.Any(row => row.Metrics?.GetValueOrDefault("tail.self.move_cycles") > 0),
            "swap did not exercise parallel movement");

        var profile = new RuntimeProfile(instructionSites: true);
        using StringWriter output = new() { NewLine = "\n" };
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output) { Profile = profile };
        using var machine = native ? HpirMachineImage.Compile(image, observe: true) : null;
        _ = machine is null ? HpirExecutor.Run(image, options) : HpirExecutor.RunMachineCandidate(machine, options);
        Require(output.ToString() == expected, $"O{level} native={native}: wanted {expected}, got {output}");
        IReadOnlyList<RuntimeProfileSample> rows = profile.Drain();
        if (deep)
        {
            var sum = rows.Single(row => row.Name == "vm.function" && row.Attributes["function.name"] == "sum");
            Require(sum.Metrics["stack_frames"] == 1
                && rows.Where(row => row.Name == "vm.function").Aggregate(0UL,
                    (count, row) => count + row.Metrics.GetValueOrDefault("stack_segment_allocations")) == 1
                && rows.Any(row => row.Name == "vm.edge" && row.Attributes["function.name"] == "sum" && row.Metrics["hits"] == 100000),
                "deep call needed a new frame rather than its physical loop");
            if (native) Require(sum.Metrics["machine_instructions"] > 100000, "x64 did not execute the loop");
        }
    }

    private static HpirProgram Cycle(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("HPIR tail: " + reason);
    }
}
