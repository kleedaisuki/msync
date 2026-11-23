using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirPathChecks
{
    private const string Lexical = """
        fn join(directory: Path, suffix: String) -> Path { directory / suffix };
        fn print_path(path: Path) { $std::io::println($std::fs::generic_string(path)) };
        let file = join(./input, "α/../測試.tar.gz");
        let normalized = $std::fs::lexically_normal(file);
        print_path(normalized);
        $std::io::println(($std::fs::filename(normalized)) as String);
        $std::io::println(($std::fs::stem(normalized)) as String);
        $std::io::println(($std::fs::extension(normalized)) as String);
        print_path($std::fs::replace_extension(normalized, ".dat"));
        print_path($std::fs::lexically_relative(normalized, ./input));
        $std::io::println(if $std::fs::has_filename(normalized) { "filename" } else { "none" });
        """;

    private const string Transfer = """
        fn entry(directory: Path, name: String) -> Path { directory / name };
        let source = entry(./input, "payload.txt");
        let first = entry(./out, "copy.txt");
        let second = entry(./out, "after.txt");
        let missing = entry(./input, "missing.txt");
        let fallback = entry(./out, "failed.txt");
        let copied = source -> first;
        let failed = missing -> fallback;
        first -> second;
        $std::io::println(if copied { "copied" } else { "copy failed" });
        $std::io::println(if failed { "unexpected success" } else { "expected failure" });
        """;

    private const string OptionalPath = """
        fn choose(name: Path, keep: Bool) -> Path? { if keep { name } else { null } };
        fn print_path(name?: Path) {
            if name is null { $std::io::println("missing"); }
            else { $std::io::println($std::fs::generic_string(name)); };
        };
        print_path(choose(./相対/test.txt, false));
        print_path(choose(./相対/test.txt, true));
        """;

    private const string PathOrString = """
        fn select_path(flag: Bool) -> Path or String {
            if flag { ./実際/report.txt } else { "literal/../typed.txt" }
        };
        fn observe(input: Path or String) {
            $std::io::println($std::fs::generic_string(input as Path));
            $std::io::println(input as String);
        };
        observe(select_path(true));
        observe(select_path(false));
        """;

    private const string PathEquality = """
        fn report(left: Path, right: Path) {
            $std::io::println((left == right) as String);
            $std::io::println((left != right) as String)
        };
        report(./x, "./x" as Path);
        report(./x, "x" as Path);
        report("./a/../x" as Path, ./x);
        report("./X" as Path, ./x);
        report("" as Path, "" as Path);
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "path-journey-" + Guid.NewGuid().ToString("N")));
        string elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        try
        {
            CheckDiscardedProgramResult(root);
            for (int level = 0; level <= 3; ++level)
            {
                CheckLexical(root, level);
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
                {
                    CheckOptional(root, level, native);
                    CheckPathOrString(root, level, native);
                    CheckPathEquality(root, level, native);
                    CheckTransfer(root, elsewhere, level, native);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR lexical Path and bound live transfer, typed Bool, failure count and dynamic dependencies, O0–O3");
    }

    private static void CheckDiscardedProgramResult(string directory)
    {
        const string source = """
            fn compute() -> Path { $std::io::println("evaluated"); ./result };
            compute()
            """;
        HpirProgram program = Prepare(source, directory, 2);
        Require(program.Functions[program.EntryPoint].Parameters.Count == 0,
            "A standalone executable entry does not require an aggregate result buffer.");
        using StringWriter output = new() { NewLine = "\n" };
        _ = HpirExecutor.Run(program, new(Output: output));
        Require(output.ToString() == "evaluated\n", "Ignoring the entry result must retain its effects.");
    }

    private static void CheckOptional(string root, int level, bool native)
    {
        using StringWriter output = new() { NewLine = "\n" };
        HpirProgram program = Prepare(OptionalPath, root, level);
        Require(program.Functions.Any(function => function.Instructions.Any(instruction => instruction.Op == HpirOp.EqU8)),
            "A genuine Path/Null choice must inspect its concrete tag.");
        var execution = new ExecutionOptions(Output: output);
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        _ = machine is null ? HpirExecutor.Run(program, execution) : HpirExecutor.RunMachineCandidate(machine, execution);
        Require(output.ToString() == "missing\n./相対/test.txt\n", $"Nullable Path O{level}: {output}");
    }

    private static void CheckPathOrString(string root, int level, bool native)
    {
        using StringWriter output = new() { NewLine = "\n" };
        HpirProgram program = Prepare(PathOrString, root, level);
        var execution = new ExecutionOptions(Output: output);
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        _ = machine is null ? HpirExecutor.Run(program, execution) : HpirExecutor.RunMachineCandidate(machine, execution);
        Require(output.ToString() == "./実際/report.txt\n./実際/report.txt\nliteral/../typed.txt\nliteral/../typed.txt\n",
            $"Path or String O{level}: {output}");
    }

    private static void CheckPathEquality(string root, int level, bool native)
    {
        using StringWriter output = new() { NewLine = "\n" };
        HpirProgram program = Prepare(PathEquality, root, level);
        var profile = new RuntimeProfile(instructionSites: true);
        var execution = new ExecutionOptions(Output: output) { Profile = profile };
        using var machine = native ? HpirMachineImage.Compile(program, observe: true) : null;
        _ = machine is null ? HpirExecutor.Run(program, execution) : HpirExecutor.RunMachineCandidate(machine, execution);
        Require(output.ToString() == "true\nfalse\nfalse\ntrue\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\n",
            $"Lexical Path equality O{level}: {output}");
        var samples = profile.Drain();
        Require(samples.Any(sample => sample.Name == "vm.native" && sample.Attributes.GetValueOrDefault("native.name") == "path.Equal")
            && samples.Any(sample => sample.Name == "vm.native" && sample.Attributes.GetValueOrDefault("native.name") == "path.NotEqual"),
            "Path equality uses a typed import");
    }

    private static void CheckLexical(string root, int level)
    {
        using StringWriter output = new() { NewLine = "\n" };
        HpirProgram program = Prepare(Lexical, root, level);
        var profile = new RuntimeProfile(instructionSites: true);
        var execution = new ExecutionOptions(Output: output) { Profile = profile };
        _ = HpirExecutor.Run(program, execution);
        const string expected = "input/測試.tar.gz\n測試.tar.gz\n測試.tar\n.gz\ninput/測試.tar.dat\n../input/測試.tar.gz\nfilename\n";
        Require(output.ToString() == expected, $"Lexical O{level}: {output}");
        Require(profile.Drain().Any(sample => sample.Name == "vm.native"
            && sample.Attributes.GetValueOrDefault("native.name") == "path.Normal"), "Path import attribution");
    }

    private static void CheckTransfer(string root, string elsewhere, int level, bool native)
    {
        string directory = Path.Combine(root, level + (native ? "-native" : "-interp"));
        Directory.CreateDirectory(Path.Combine(directory, "input"));
        File.WriteAllText(Path.Combine(directory, "input", "payload.txt"), "live bytes");
        HpirProgram program = Prepare(Transfer, directory, level);
        Require(program.Imports.Any(import => import.Service == HpirService.LiveTransfer && import.Result == HpirWidth.U64)
            && program.Imports.Any(import => import.Service == HpirService.LiveTransfer && import.Result == HpirWidth.Void)
            && program.PreparedTransfers.Count == 0, "Observed and discarded live transfers use typed services, not binding");
        using StringWriter output = new() { NewLine = "\n" };
        var errors = new ConcurrentQueue<MsyncException>();
        var execution = new ExecutionOptions(WorkingDirectory: elsewhere, Output: output) { ReportError = errors.Enqueue };
        HpirRunOutcome result;
        if (native)
        {
            using var image = HpirMachineImage.Compile(program);
            result = HpirExecutor.RunMachineOutcome(image, execution);
        }
        else result = HpirExecutor.RunOutcome(program, execution);
        Require(output.ToString() == "copied\nexpected failure\n", $"Live transfer result O{level}: {output}");
        Require(result.FailedTransfers == 1 && errors.TryDequeue(out var diagnostic) && errors.IsEmpty
            && diagnostic.Message.Contains("missing.txt", StringComparison.Ordinal) && diagnostic.Span is not null,
            "A failed transfer should be a false result and exactly one source-attributed failure.");
        Require(File.ReadAllText(Path.Combine(directory, "out", "after.txt")) == "live bytes"
            && !Directory.Exists(Path.Combine(elsewhere, "out")), "Runtime dependencies and bound directory");
    }

    private static HpirProgram Prepare(string source, string directory, int level)
    {
        var module = Compiler.Analyze(source, Path.Combine(directory, "path-source.msync"));
        HpirProgram portable = Roundtrip(Compiler.LowerHpir(module, level));
        HpirProgram bound = HpirBinding.Prepare(portable, new(directory, directory));
        HpirProgram loaded = Roundtrip(bound);
        Require(loaded.Context?.WorkingDirectory == directory && loaded.PathAnchors, "Bound path anchor survived the wire");
        return loaded;
    }

    private static HpirProgram Roundtrip(HpirProgram program)
    {
        using var bytes = new MemoryStream();
        HpirArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
