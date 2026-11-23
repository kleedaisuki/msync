using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTransferChecks
{
    private const string Selection = "./inputs / <.*\\.txt> -> ./selected/;";

    internal static bool ProcessCommand(string[] args)
    {
        switch (args)
        {
            case ["effect-test"]:
                Run();
                return true;
            case ["context-check-test"]:
                string contextRoot = RepositoryRoot();
                CheckContextCheck(contextRoot);
                return true;
            case ["effect-compile", string source, string artifact, string optimization]:
                Compile(source, artifact, int.Parse(optimization, CultureInfo.InvariantCulture));
                return true;
            case ["effect-bind", string artifact, string executable]:
                Bind(artifact, executable);
                return true;
            case ["effect-run", string executable]:
                Execute(executable);
                return true;
            default:
                return false;
        }
    }

    internal static void Run()
    {
        string root = Path.Combine(RepositoryRoot(), ".temp", "hpir", "context-journey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CheckFrozen(root);
        CheckEmpty(root);
        CheckResidual(root);
        CheckLiteralTransfers(root);
        CheckSharedOperands(root);
        CheckSequential(root);
        CheckUnmodeledFrontier();
        CheckContextCheck(RepositoryRoot());
        Directory.Delete(root, true);
        Console.WriteLine("PASS detached HPIR transfers: membership, live bytes, bound context and ordered work");
    }

    private static void CheckContextCheck(string repository)
    {
        const string branch = """
            if exists ./choose-first { ./first.txt -> ./chosen.txt; }
            else { ./second.txt -> ./chosen.txt; };
            """;
        string root = Path.Combine(repository, ".temp", "hpir", "context-check-" + Guid.NewGuid().ToString("N"));
        string elsewhere = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                string directory = Directory.CreateDirectory(Path.Combine(root, "O" + level)).FullName;
                File.WriteAllText(Path.Combine(directory, "first.txt"), "first");
                File.WriteAllText(Path.Combine(directory, "second.txt"), "second");
                string marker = Path.Combine(directory, "choose-first"), chosen = Path.Combine(directory, "chosen.txt");
                string source = Path.Combine(directory, "contextual-branch.msync");
                File.WriteAllText(source, branch);
                HpirProgram portable = Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(source), source), level);
                string objectFile = Path.Combine(directory, "branch.mso"), image = Path.Combine(directory, "branch.mexe");
                Write(objectFile, portable);
                File.Delete(source);
                // Binding may observe this marker, but may not execute either arm.
                File.WriteAllText(marker, "present while binding");
                var trace = new List<CompilationObservation>();
                HpirProgram bound = HpirBinding.Prepare(Load(objectFile), new(directory, HostContext.HomeDirectory),
                    trace: new(trace.Add, includeOptimizerPasses: true));
                Write(image, bound);
                Require(bound.ContextChecks is [{ Value: true }] && !File.Exists(chosen)
                    && trace.Any(item => item.Name == "backend.hpir.bind.context_check"
                        && item.Attributes?.GetValueOrDefault("source.file") == source),
                    $"Binding did not retain the one contextual decision at O{level}.");
                File.Delete(marker);
                File.Delete(objectFile);
                HpirProgram loaded = Load(image);
                using var machine = HpirMachineImage.IsSupported ? HpirMachineImage.Compile(loaded) : null;
                var options = new ExecutionOptions(WorkingDirectory: elsewhere);
                _ = HpirExecutor.Run(loaded, options);
                Require(File.ReadAllText(chosen) == "first" && !File.Exists(Path.Combine(elsewhere, "chosen.txt")),
                    $"Bound execution reobserved the marker at O{level}.");
                if (machine is not null)
                {
                    File.Delete(chosen);
                    _ = HpirExecutor.RunMachineCandidate(machine, options);
                    Require(File.ReadAllText(chosen) == "first", "Machine execution reobserved a bound predicate.");
                }

                // A writer ahead of the same predicate makes it an actual run-time observation.
                HpirProgram residual = Compiler.LowerHpir(
                    Compiler.Analyze("\"fresh\" -> ./choose-first;\n" + branch, source), level);
                Require(residual.ContextChecks.Count == 0, "A preceding writer forbids the bind observation.");
                File.Delete(chosen);
                var live = HpirExecutor.RunOutcome(HpirBinding.Prepare(residual, new(directory, HostContext.HomeDirectory)), options);
                Require(live.FailedTransfers == 0 && File.ReadAllText(chosen) == "first",
                    "The live predicate did not wait for its actual writer.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR detached contextual predicate, unchanged code, and actual writer frontier O0–O3");
    }

    private static void CheckFrozen(string root)
    {
        var fixture = new Fixture(root, "frozen");
        var input = Directory.CreateDirectory(Path.Combine(fixture.Anchor, "inputs"));
        string first = Path.Combine(input.FullName, "first.txt");
        File.WriteAllText(first, "old bytes");
        File.WriteAllText(Path.Combine(input.FullName, "ignored.bin"), "not selected");
        fixture.Build(Selection, 2);
        var effects = Load(fixture.Executable).PreparedTransfers;
        Require(effects.Count == 1 && effects[0].Pattern is null
            && effects[0].Paths.Count == 1 && Path.GetFileName(effects[0].Paths[0]) == "first.txt", "prepared membership and predicate erasure");
        Require(!Directory.Exists(Path.Combine(fixture.Anchor, "selected")), "binding did not publish a destination");

        File.WriteAllText(first, "live bytes");
        File.WriteAllText(Path.Combine(input.FullName, "later.txt"), "must not be copied");
        var result = fixture.Execute();
        Require(result.Success && result.Output.Contains("frozen=1 live=0", StringComparison.Ordinal), result.ToString());
        string selected = Path.Combine(fixture.Anchor, "selected");
        Require(File.ReadAllText(Path.Combine(selected, "first.txt")) == "live bytes"
            && !File.Exists(Path.Combine(selected, "later.txt"))
            && !File.Exists(Path.Combine(selected, "ignored.bin"))
            && !Directory.Exists(Path.Combine(fixture.Runner, "selected")), "bound directory, frozen names, and live bytes");

        File.Delete(first);
        var failure = fixture.Execute();
        Require(!failure.Success && failure.Error.Contains("first.txt", StringComparison.Ordinal)
            && failure.Error.Contains("program.msync", StringComparison.Ordinal), "failed prepared transfer names resource and source");
    }

    private static void CheckEmpty(string root)
    {
        var fixture = new Fixture(root, "empty");
        string input = Directory.CreateDirectory(Path.Combine(fixture.Anchor, "inputs")).FullName;
        fixture.Build(Selection, 1);
        var transfer = Load(fixture.Executable).PreparedTransfers.Single();
        Require(transfer.Pattern is null && transfer.Paths.Count == 0, "observed empty selection is not residual");
        File.WriteAllText(Path.Combine(input, "later.txt"), "not in frozen selection");
        var result = fixture.Execute();
        Require(result.Success && !Directory.Exists(Path.Combine(fixture.Anchor, "selected")), "no replanning for frozen empty selection");
    }

    private static void CheckResidual(string root)
    {
        var fixture = new Fixture(root, "residual");
        fixture.Build(Selection, 3);
        var transfer = Load(fixture.Executable).PreparedTransfers.Single();
        Require(transfer.Pattern is not null && transfer.Paths.Count == 1, "missing bind-time directory is a residual effect");
        string input = Directory.CreateDirectory(Path.Combine(fixture.Anchor, "inputs")).FullName;
        File.WriteAllText(Path.Combine(input, "arrived.txt"), "present at execution");
        var result = fixture.Execute();
        Require(result.Success && result.Output.Contains("frozen=0 live=1", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(fixture.Anchor, "selected", "arrived.txt")) == "present at execution", result.ToString());
    }

    private static void CheckLiteralTransfers(string root)
    {
        var write = new Fixture(root, "write");
        write.Build("\"literal text\" -> ./selected/result.txt;", 0);
        Require(!Directory.Exists(Path.Combine(write.Anchor, "selected")), "literal bytes are not published at bind");
        var wrote = write.Execute();
        Require(wrote.Success && File.ReadAllText(Path.Combine(write.Anchor, "selected", "result.txt")) == "literal text", wrote.ToString());

        var copy = new Fixture(root, "copy");
        copy.Build("./input.txt -> ./selected/;", 0);
        File.WriteAllText(Path.Combine(copy.Anchor, "input.txt"), "copy bytes");
        var copied = copy.Execute();
        Require(copied.Success && File.ReadAllText(Path.Combine(copy.Anchor, "selected", "input.txt")) == "copy bytes", copied.ToString());
    }

    private static void CheckSharedOperands(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "shared-operands")).FullName;
        const string code = """
            let payload = "observable once";
            $std::io::println(payload);
            payload -> ./published.txt;
            """;
        List<CompilationObservation> compile = [];
        var trace = new CompilationTrace(compile.Add, includeOptimizerPasses: true);
        string source = Path.Combine(directory, "shared.msync");
        var portable = Compiler.LowerHpir(Compiler.Analyze(code, source), 2, trace);
        Require(portable.TransferInputs.Count == 1 && compile.Any(item => item.Name == "compiler.hpir.select.function"
            && item.Metrics?.GetValueOrDefault("effects.shared_operands_retained") > 0), "shared ordinary use must not reject a bound effect");
        string objectFile = Path.Combine(directory, "shared.mso"), image = Path.Combine(directory, "shared.mexe");
        Write(objectFile, portable);
        var context = new BindingContext(directory, HostContext.HomeDirectory, source);
        Write(image, HpirBinding.Prepare(Load(objectFile), context));
        string outputFile = Path.Combine(directory, "published.txt");
        Require(!File.Exists(outputFile), "binding executed a shared operand's transfer");
        using StringWriter output = new() { NewLine = "\n" };
        _ = HpirExecutor.Run(Load(image), new(Output: output));
        Require(output.ToString() == "observable once\n" && File.ReadAllText(outputFile) == "observable once",
            "the retained value must be used by both its run-time call and the linked plan");
    }

    private static void CheckSequential(string root)
    {
        var affected = new Fixture(root, "write-before-selection");
        string input = Directory.CreateDirectory(Path.Combine(affected.Anchor, "inputs")).FullName;
        File.WriteAllText(Path.Combine(input, "initial.txt"), "initial");
        affected.Build("\"same-run bytes\" -> ./inputs/produced.txt; " + Selection, 2);
        var effects = Load(affected.Executable).PreparedTransfers;
        Require(effects.Count == 2 && effects[1].Pattern is not null && effects[1].Predecessors.SequenceEqual([0]),
            "affected selection must be residual and depend on the preceding writer");
        Require(!File.Exists(Path.Combine(input, "produced.txt")), "binding did not execute the writer");
        var result = affected.Execute();
        Require(result.Success && result.Output.Contains("frozen=0 live=1 deps=1", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(affected.Anchor, "selected", "produced.txt")) == "same-run bytes"
            && File.ReadAllText(Path.Combine(affected.Anchor, "selected", "initial.txt")) == "initial", result.ToString());

        var disjoint = new Fixture(root, "independent-before-selection");
        input = Directory.CreateDirectory(Path.Combine(disjoint.Anchor, "inputs")).FullName;
        File.WriteAllText(Path.Combine(input, "known.txt"), "frozen membership");
        disjoint.Build("\"other tree\" -> ./other/result.txt; " + Selection, 2);
        var safe = Load(disjoint.Executable).PreparedTransfers[1];
        Require(safe.Pattern is null && safe.Paths.Count == 1 && safe.Predecessors.Count == 0,
            "independent exact write must not suppress a safe observation or add a wait");
        File.WriteAllText(Path.Combine(input, "later.txt"), "excluded");
        result = disjoint.Execute();
        Require(result.Success && File.Exists(Path.Combine(disjoint.Anchor, "other", "result.txt"))
            && File.Exists(Path.Combine(disjoint.Anchor, "selected", "known.txt"))
            && !File.Exists(Path.Combine(disjoint.Anchor, "selected", "later.txt")), result.ToString());

        var failed = new Fixture(root, "failed-writer");
        input = Directory.CreateDirectory(Path.Combine(failed.Anchor, "inputs")).FullName;
        Directory.CreateDirectory(Path.Combine(input, "blocked"));
        File.WriteAllText(Path.Combine(input, "still.txt"), "independent surviving input");
        failed.Build("\"fails\" -> ./inputs/blocked; " + Selection, 2);
        result = failed.Execute();
        Require(!result.Success && result.Error.Contains("blocked", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(failed.Anchor, "selected", "still.txt")) == "independent surviving input",
            "a failed predecessor reports once and does not suppress separately admitted work");
    }

    private static void CheckUnmodeledFrontier()
    {
        List<CompilationObservation> events = [];
        var trace = new CompilationTrace(events.Add, includeOptimizerPasses: true);
        const string precedingWrite = "fn hidden() { \"created\" -> ./inputs/new.txt; }; hidden(); ";
        var program = Compiler.LowerHpir(Compiler.Analyze(precedingWrite + Selection), 2, trace);
        Require(program.TransferInputs.Count == 0 && program.Imports.Any(import => import.Service == HpirService.LiveTransferSelection)
            && events.Any(item => item.Metrics?.GetValueOrDefault("effects.transfer.rejected_frontier") == 1),
            "An unmodeled preceding write keeps a runtime selection rather than a stale bound membership");
    }

    private static void Compile(string source, string portable, int level)
    {
        List<CompilationObservation> events = [];
        var trace = new CompilationTrace(events.Add, includeOptimizerPasses: true);
        var program = Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(source), source, trace), level, trace);
        var body = program.Functions[program.EntryPoint];
        int transfers = program.TransferInputs.Count;
        Require(program.Stage == HpirStage.Relocatable && transfers > 0 && program.PreparedTransfers.Count == 0
            && body.Instructions.Length == transfers + 1 && body.Instructions[^1].Op == HpirOp.Return,
            "expected only producer-selected service CALLs and return");
        for (int index = 0; index < transfers; index++)
        {
            HpirInstruction call = body.Instructions[index];
            Require(call.Op == HpirOp.Call && program.Imports[~body.Calls[call.A].Target] is
                { Service: HpirService.Transfer, Binding: var binding } && binding == index,
                "physical transfer calls lost source order or binding identity");
        }
        Require(events.Any(item => item.Name == "compiler.hpir.select.function"
            && item.Metrics?.GetValueOrDefault("effects.sliced_operands") > 0), "producer effect selection attributed to function");
        Write(portable, program);
    }

    private static void Bind(string portable, string executable)
    {
        HpirProgram program = Load(portable);
        Require(program.TransferInputs.All(input => !File.Exists(input.Source!.Value.Source)), "binding runs with no source input");
        List<CompilationObservation> events = [];
        var context = new BindingContext(Environment.CurrentDirectory, HostContext.HomeDirectory);
        var prepared = HpirBinding.Prepare(program, context, trace: new(events.Add, includeOptimizerPasses: true));
        Require(prepared.Stage == HpirStage.Executable && prepared.TransferInputs.Count == 0
            && ReferenceEquals(program.Functions, prepared.Functions) && ReferenceEquals(program.Imports, prepared.Imports),
            "binding only attached prepared metadata");
        Require(events.Any(item => item.Name == "backend.hpir.bind.transfer"
            && item.Attributes?.ContainsKey("source.file") == true), "binding transfer attribution");
        Write(executable, prepared);
    }

    private static void Execute(string executable)
    {
        ConcurrentQueue<RuntimeTimelineRecord> events = new();
        try
        {
            HpirExecutor.Run(Load(executable), new() { Timeline = new(events.Enqueue, "detached-effect") });
        }
        catch (MsyncException error)
        {
            var failed = events.Single(item => item.Name == "hpir.effect.transfer" && item.Outcome == "error");
            Require(failed.Error is not null
                && failed.Attributes.ContainsKey("source.file"), "failing transfer retained causal source context");
            Console.Error.WriteLine($"{error.Span?.Source}: {error.Message}");
            Environment.ExitCode = 1;
            return;
        }
        foreach (var transfer in events.Where(item => item.Name == "hpir.effect.transfer")
            .OrderBy(item => item.Metrics["transfer.binding"]))
        {
            Require(transfer.Outcome == "ok" && transfer.Attributes.ContainsKey("source.file")
                && transfer.Attributes.ContainsKey("function.name"), "run submits linked effect with source context");
            Console.WriteLine($"frozen={transfer.Metrics["selection.frozen_members"]} live={transfer.Metrics["selection.live"]} deps={transfer.Metrics["dependencies.prepared"]}");
        }
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "msync.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run the HPIR pipeline journey inside the repository.");
    }

    private static void Write(string path, HpirProgram program)
    {
        using FileStream stream = File.Create(path);
        HpirArtifact.WriteProduced(program, stream);
    }

    private static HpirProgram Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return HpirArtifact.ReadTrusted(stream);
    }

    private static ChildResult Child(string directory, params string[] args)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("HPIR test process has no executable path.");
        // User-mode emulators need an explicit launcher when the host has no
        // binfmt handler for the target executable.
        string? launcher = Environment.GetEnvironmentVariable("MSYNC_HPIR_TEST_LAUNCHER");
        var start = new ProcessStartInfo(launcher ?? processPath)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (launcher is not null) start.ArgumentList.Add(processPath);
        if (Path.GetFileNameWithoutExtension(processPath) == "dotnet") start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch HPIR test child.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException($"HPIR test subprocess timed out: {string.Join(' ', args)}");
        }
        return new(child.ExitCode == 0, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private sealed class Fixture
    {
        internal string Anchor { get; }
        internal string Runner { get; }
        private string Producer { get; }
        private string Source { get; }
        private string Portable { get; }
        internal string Executable { get; }

        internal Fixture(string root, string name)
        {
            string work = Path.Combine(root, name);
            Anchor = Directory.CreateDirectory(Path.Combine(work, "anchor")).FullName;
            Runner = Directory.CreateDirectory(Path.Combine(work, "runner")).FullName;
            Producer = Directory.CreateDirectory(Path.Combine(work, "producer")).FullName;
            Source = Path.Combine(Producer, "program.msync");
            Portable = Path.Combine(Producer, "program.mso");
            Executable = Path.Combine(Runner, "program.mexe");
        }

        internal void Build(string source, int level)
        {
            File.WriteAllText(Source, source);
            ChildResult compiled = Child(Producer, "effect-compile", Source, Portable, level.ToString(CultureInfo.InvariantCulture));
            Require(compiled.Success, compiled.ToString());
            File.Delete(Source);
            ChildResult bound = Child(Anchor, "effect-bind", Portable, Executable);
            Require(bound.Success, bound.ToString());
            File.Delete(Portable);
        }

        internal ChildResult Execute() => Child(Runner, "effect-run", Executable);
    }

    private readonly record struct ChildResult(bool Success, string Output, string Error);
}
