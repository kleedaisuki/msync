using System.Collections.Concurrent;
using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFileWriteChecks
{
    private const string Name = "file-write.msync";
    private const string Shared = """
        ./first.txt -> ./shared.txt;
        $std::io::append_text("./shared.txt", "|β");
        ./shared.txt -> ./witness.txt;
        $std::io::write_text(./shared.txt, "final 🧪");
        ./shared.txt -> ./after.txt;
        $std::io::write_text($std::io::read_text("./destination.txt"), $std::io::read_text(./shared.txt));
        """;
    private const string Live = """
        let child = release /bin/sh("-c", "while [ ! -f release ]; do sleep 0.01; done; printf live");
        let copied = child::stdout -> ./shared.txt;
        $std::io::println("before");
        $std::io::append_text(./shared.txt, "|append");
        $std::io::write_text("./receipt.txt", copied as String);
        $std::io::println("after");
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", $"file-write-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        string elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
        try
        {
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                HpirProgram program = Compile(Shared, level, root);
                foreach (bool native in Engines()) CheckShared(program, root, elsewhere, native);
            }
            CheckFailures(root);
            CheckPathUnion(root);
            CheckDryRun(root);
            if (!OperatingSystem.IsWindows())
            {
                HpirProgram gated = Compile(Live, 2, root);
                foreach (bool native in Engines()) CheckLive(gated, root, native);
                CheckLive(gated, root, native: false, cancel: true);
                CheckSymlink(root);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS detached file text replace/append, typed input, fatal source, symlink and shared/live ordering");
    }

    private static void CheckShared(HpirProgram program, string root, string elsewhere, bool native)
    {
        File.WriteAllText(Path.Combine(root, "first.txt"), "seed");
        File.WriteAllText(Path.Combine(root, "destination.txt"), "./nested/mirrored.txt");
        foreach (string name in new[] { "shared.txt", "witness.txt", "after.txt", "nested/mirrored.txt" })
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path)) File.Delete(path);
        }
        Require(program.Imports.Count(item => item.Service == HpirService.SharedTransfer) == 3
            && program.Imports.Any(item => item.Service == HpirService.WriteFileText
                && HpirFileTextWriteImports.Decode(item.Binding).Source == HpirFileTextSource.Completion),
            "A file write must join the shared prepared frontier without admitting its operands at bind.");
        Execute(program, new(WorkingDirectory: elsewhere), native);
        CheckBytes(root, "witness.txt", "seed|β");
        CheckBytes(root, "shared.txt", "final 🧪");
        CheckBytes(root, "after.txt", "final 🧪");
        CheckBytes(root, "nested/mirrored.txt", "final 🧪");
        Require(!File.Exists(Path.Combine(elsewhere, "shared.txt")), "Run-time cwd replaced the bound directory.");
    }

    private static void CheckLive(HpirProgram program, string root, bool native, bool cancel = false)
    {
        foreach (string name in new[] { "release", "shared.txt", "receipt.txt" }) File.Delete(Path.Combine(root, name));
        using var output = new StringWriter { NewLine = "\n" };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        {
            Timeline = new(item =>
            {
                observations.Enqueue(item);
                if (item.Name == "runtime.dependency"
                    && item.Attributes.GetValueOrDefault("dependency.kind") == "file_write.predecessor")
                    blocked.TrySetResult();
            }, "file-write")
        };
        Task run = Task.Run(() => Execute(program, options, native, stop.Token));
        try
        {
            Task first = Task.WhenAny(blocked.Task, run).WaitAsync(stop.Token).GetAwaiter().GetResult();
            if (first == run) run.GetAwaiter().GetResult();
            Require(first == blocked.Task && output.ToString() == "before\n"
                && !File.Exists(Path.Combine(root, "receipt.txt")),
                "The explicit write must wait for the matching live transfer before the next guest effect.");
            if (cancel)
            {
                stop.Cancel();
                try { run.GetAwaiter().GetResult(); throw new Exception("The file write ignored cancellation."); }
                catch (OperationCanceledException) { }
                Require(!File.Exists(Path.Combine(root, "shared.txt"))
                    && !File.Exists(Path.Combine(root, "receipt.txt")) && output.ToString() == "before\n",
                    "Cancellation left an admitted file write or advanced the guest.");
                return;
            }
            File.WriteAllText(Path.Combine(root, "release"), "");
            run.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(output.ToString() == "before\nafter\n"
                && observations.Any(item => item.Name == "hpir.file_write.write"
                    && item.Attributes.GetValueOrDefault("file.operation") == "io.append_text"
                    && item.Metrics.GetValueOrDefault("text.characters") == 7)
                && program.Imports.Any(item => item.Service == HpirService.WriteFileText
                    && HpirFileTextWriteImports.Decode(item.Binding).Source == HpirFileTextSource.Receipt),
                "The completed explicit write lost its actual effect or typed receipt.");
            CheckBytes(root, "shared.txt", "live|append");
            CheckBytes(root, "receipt.txt", "true");
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
    }

    private static void CheckFailures(string root)
    {
        File.WriteAllText(Path.Combine(root, "not-dir"), "block");
        Fault("""
            $std::io::write_text("./not-dir/child.txt", "body");
            $std::io::println("never");
            """, "write_text", typeof(IOException));
        Fault("""
            $std::io::append_text(./unexpected.txt, $std::io::read_text(./missing.txt));
            $std::io::println("never");
            """, "read_text", typeof(FileNotFoundException));
        Require(!File.Exists(Path.Combine(root, "unexpected.txt")), "A failed operand must not admit the write.");

        void Fault(string source, string origin, Type cause)
        {
            using var output = new StringWriter();
            HpirProgram program = Compile(source, 2, root);
            try
            {
                Execute(program, new(Output: output), false);
                throw new Exception("Expected an explicit file operation failure.");
            }
            catch (MsyncException error)
            {
                Require(error.Span is { Source: Name } span
                    && source.Substring(span.Start, span.Length).Contains(origin, StringComparison.Ordinal)
                    && error.InnerException is { } inner && cause.IsAssignableFrom(inner.GetType())
                    && output.ToString() == "", "Write failure was treated as a nonfatal transfer or lost its originating cause.");
            }
        }
    }

    private static void CheckPathUnion(string root)
    {
        const string program = """
            fn destination(path: Bool) {
                if path { ./selected-path.txt } else { "./selected-text.txt" }
            };
            $std::io::write_text(destination(true), "Path");
            $std::io::append_text(destination(false), "String");
            """;
        Execute(Compile(program, 2, root), new(), false);
        CheckBytes(root, "selected-path.txt", "Path");
        CheckBytes(root, "selected-text.txt", "String");
    }

    private static void CheckSymlink(string root)
    {
        string link = Path.Combine(root, "link.txt");
        File.WriteAllText(Path.Combine(root, "referent.txt"), "first");
        File.CreateSymbolicLink(link, "referent.txt");
        const string program = """
            $std::io::write_text(./link.txt, "replace");
            $std::io::append_text("./link.txt", "|append");
            """;
        Execute(Compile(program, 2, root), new(), false);
        CheckBytes(root, "referent.txt", "replace|append");
        Require(new FileInfo(link).LinkTarget == "referent.txt", "Explicit overwrite must follow, not replace, the symlink.");
    }

    private static void CheckDryRun(string root)
    {
        File.WriteAllText(Path.Combine(root, "kept.txt"), "before");
        const string source = """
            $std::io::write_text("./not-created.txt", "no");
            $std::io::append_text(./kept.txt, "|no");
            """;
        Execute(Compile(source, 2, root), new(DryRun: true), false);
        Require(!File.Exists(Path.Combine(root, "not-created.txt")), "Dry run introduced an explicit file.");
        CheckBytes(root, "kept.txt", "before");
    }

    private static HpirProgram Compile(string source, int level, string root)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        var module = Compiler.Analyze(source, Name, trace);
        HpirProgram portable = Reload(Compiler.LowerHpir(module, level, trace));
        Require(observations.Any(item => item.Metrics?.GetValueOrDefault("paths.file_text_writes") > 0),
            "Explicit file output must retain source-owned selection telemetry.");
        return Reload(HpirBinding.Prepare(portable, new(root, root)));
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void CheckBytes(string root, string name, string expected) =>
        Require(File.ReadAllBytes(Path.Combine(root, name)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            $"Wrong UTF-8 bytes in {name}.");

    private static IEnumerable<bool> Engines()
    {
        yield return false;
        if (HpirMachineImage.IsSupported) yield return true;
    }

    private static void Execute(HpirProgram program, ExecutionOptions options, bool native, CancellationToken token = default)
    {
        HpirRunOutcome result;
        if (!native) result = HpirExecutor.RunOutcome(program, options, token);
        else
        {
            using var image = HpirMachineImage.Compile(program);
            result = HpirExecutor.RunMachineOutcome(image, options, token);
        }
        Require(result.FailedTransfers == 0, "A matching transfer failed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
