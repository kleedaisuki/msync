using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirDirectoryAuthorityChecks
{
    private const string Source = "directory-authority.msync";
    private const string Script = """
        fn to_path(name: String) -> Path { name as Path };
        let original = ./old-value.txt;
        let before = $std::shell::pwd();
        $std::io::write_text("early.txt", "before change");
        let entered = $std::shell::cd("next");
        let after = $std::shell::pwd();
        $std::io::write_text("dynamic.txt", "live String");
        $std::io::write_text(to_path("converted.txt"), "live conversion");
        $std::io::write_text(original, "saved Path");
        "bound late Path" -> ./late-literal.txt;
        $std::io::println(before as String);
        $std::io::println(entered as String);
        $std::io::println(after as String);
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "directory-authority-" + Guid.NewGuid().ToString("N")));
        string original = Environment.CurrentDirectory;
        Directory.CreateDirectory(root);
        try
        {
            foreach (int level in new[] { 0, 3 })
            {
                CheckStandalone(Workspace(root, $"image-{level}"), Workspace(root, "detached"), level);
                CheckSession(Workspace(root, $"session-{level}"), level);
            }
            CheckChild(Workspace(root, "child"), Workspace(root, "detached"));
            Require(Environment.CurrentDirectory == original, "Language cd changed the process-wide working directory.");
            Console.WriteLine("PASS HPIR directory authority: bound Path origins, dynamic String paths, actual shell cd/pwd, selected preparation and failure across O0/O3");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckStandalone(string hpir, string detached, int optimization)
    {
        string script = Script + (OperatingSystem.IsLinux() && File.Exists("/bin/pwd")
            ? "\nlet subprocess = /bin/pwd(); subprocess::stdout -> ./child-cwd.txt;"
            : "");
        var relocatable = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(script, Source), optimization));
        Require(relocatable.Imports.Any(import => import.Service == HpirService.Path
            && HpirPathImports.Decode(import.Binding).Op == HpirPathOp.SetWorkingDirectory)
            && relocatable.TransferInputs.Count == 0, "Binding prepared a transfer across the actual change frontier.");
        var bound = RoundTrip(HpirBinding.Prepare(relocatable, new(hpir, hpir)));
        using var actual = new StringWriter { NewLine = "\n" };
        List<RuntimeTimelineRecord> trace = [];
        List<string> changes = [];
        _ = HpirExecutor.RunConfigured(bound, new(WorkingDirectory: detached, Output: actual)
        {
            WorkingDirectoryChanged = changes.Add, Timeline = new(trace.Add, "directory-authority")
        });
        CheckFiles(hpir);
        string next = Path.Combine(hpir, "next");
        Require(actual.ToString() == $"{hpir}\n{next}\n{next}\n"
            && changes.SequenceEqual([next]), "Detached artifact changed cd/pwd behavior.");
        if (script.Length != Script.Length)
            Require(File.ReadAllText(Path.Combine(hpir, "child-cwd.txt")).TrimEnd() == next,
                "A process admitted after cd did not inherit the selected directory.");
        Require(trace.Any(record => record.Name == "hpir.directory.change" && record.Attributes.GetValueOrDefault("directory.current") == next),
            "Missing actual directory-mutation telemetry.");
        if (HpirMachineImage.IsSupported && Environment.GetEnvironmentVariable("MSYNC_JIT") == "force")
            Require(trace.Any(record => record.Name == "hpir.machine.publish" && record.Metrics.GetValueOrDefault("code.bytes") > 0),
                "Forced artifact execution did not publish machine code.");
        var unselected = Compiler.LowerHpir(Compiler.Analyze("""
            if false { $std::shell::cd("not selected"); };
            "constant" -> ./prepared.txt;
            """, Source), optimization);
        Require(unselected.TransferInputs.Count == 1, "An unreachable directory change blocked literal preparation.");
    }

    private static void CheckFiles(string root)
    {
        string next = Path.Combine(root, "next");
        Require(File.ReadAllText(Path.Combine(root, "early.txt")) == "before change"
            && File.ReadAllText(Path.Combine(root, "old-value.txt")) == "saved Path"
            && File.ReadAllText(Path.Combine(root, "late-literal.txt")) == "bound late Path"
            && File.ReadAllText(Path.Combine(next, "dynamic.txt")) == "live String"
            && File.ReadAllText(Path.Combine(next, "converted.txt")) == "live conversion", "Wrong bound or current Path context.");
    }

    private static void CheckSession(string root, int optimization)
    {
        string next = Path.Combine(root, "next"), sibling = Path.Combine(root, "sibling");
        Directory.CreateDirectory(sibling);
        List<string> changed = [];
        var compiler = new CompilerSession(Source, optimization);
        using var runtime = new HpirExecutionSession(new(WorkingDirectory: root) { WorkingDirectoryChanged = changed.Add });
        _ = Submit(HpirSessionChecks.ShellPrelude());
        Require(Submit("let retained = ./kept.txt; let other = ./sibling; pwd()").Path?.Raw == root,
            "The actual shell pwd must read the initial session authority.");
        Require(Submit("cd(\"next\"); $std::io::write_text(\"current.txt\", \"changed\"); pwd()").Path?.Raw == next,
            "The actual shell pwd after cd in the same submission must read the live authority.");
        _ = Submit("$std::io::write_text(retained, \"original\"); null");
        Require(File.ReadAllText(Path.Combine(root, "kept.txt")) == "original"
            && File.ReadAllText(Path.Combine(next, "current.txt")) == "changed"
            && Submit("pwd()").Path?.Raw == next, "Saved Paths and subsequent submissions lost their distinct contexts.");
        var failed = compiler.CompilePhysical("cd(other); cd(\"missing\"); null");
        try
        {
            _ = runtime.Execute(failed);
            throw new InvalidOperationException("The missing directory unexpectedly succeeded.");
        }
        catch (MsyncException error) when (error.InnerException is FileNotFoundException or DirectoryNotFoundException)
        {
            compiler.ReconcilePhysicalSubmission(runtime.IsInitialized, runtime.ObserveBinding);
        }
        Require(runtime.WorkingDirectory == sibling && Submit("pwd()").Path?.Raw == sibling
            && changed.SequenceEqual([next, sibling]),
            "Changing to a retained Path or keeping the successful prefix after failure was lost.");

        HpirSessionValue Submit(string text)
        {
            var value = runtime.Execute(compiler.CompilePhysical(text));
            compiler.AcceptPhysicalSubmission(runtime.ObserveBinding);
            return value;
        }
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private static void CheckChild(string root, string detached)
    {
        const string source = """
            fn callback(n: Number) -> Number {
                let line? = $std::io::read_line();
                if line? { $std::shell::cd("sibling"); n + 2 } else { 0 }
            };
            let values = $std::ranges::transform($std::ranges::iota(1, 3), callback);
            let front = $std::ranges::front(values);
            $std::shell::cd("next");
            $std::io::write_text("parent-before.txt", $std::shell::pwd() as String);
            $std::io::println("queued");
            $std::io::println(front as String);
            $std::io::write_text("parent-after.txt", $std::shell::pwd() as String);
            """;
        string parent = Path.Combine(root, "next"), child = Path.Combine(root, "sibling");
        Directory.CreateDirectory(child);
        var program = RoundTrip(HpirBinding.Prepare(RoundTrip(Compiler.LowerHpir(
            Compiler.Analyze(source, Source), 2)), new(root, root)));
        Require(program.Imports.Any(import => import.Service == HpirService.GuestNumber),
            "The directory fixture must actually exercise cooperative suspension.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var reader = new GatedInput();
        using var output = new MarkerWriter { NewLine = "\n" };
        List<string> changes = [];
        var run = Task.Run(() => HpirExecutor.RunConfigured(program,
            new(WorkingDirectory: detached, Output: output) { Input = reader, WorkingDirectoryChanged = changes.Add }, stop.Token));
        try
        {
            output.Marker.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(!run.IsCompleted, "The child must still be held when the parent changes directory.");
            reader.Line.SetResult(null);
            _ = run.WaitAsync(stop.Token).GetAwaiter().GetResult();
        }
        finally
        {
            if (!run.IsCompleted) stop.Cancel();
            reader.Line.TrySetResult(null);
        }
        Require(File.ReadAllText(Path.Combine(parent, "parent-before.txt")) == parent
            && File.ReadAllText(Path.Combine(parent, "parent-after.txt")) == parent
            && changes.SequenceEqual([parent, child])
            && output.ToString() == "queued\n3\n", "The parked child lost its own captured directory or mutated its parent.");
    }

    private sealed class GatedInput : TextReader, IAsyncLineInput
    {
        internal readonly TaskCompletionSource<string?> Line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) => new(Line.Task.WaitAsync(token));
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal readonly TaskCompletionSource Marker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            WriteLine(text.Span);
            if (text.Span.SequenceEqual("queued")) Marker.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static string Workspace(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "next"));
        return directory;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
