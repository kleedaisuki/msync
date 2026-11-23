using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFileTextChecks
{
    private const string Name = "live-file-text.msync";

    internal static void Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "hpir", $"file-text-{Guid.NewGuid():N}");
        string detached = Path.Combine(root, "detached");
        Directory.CreateDirectory(detached);
        try
        {
            ContentChain(root, detached);
            MixedResults(root, detached);
            SourceFailure(root, detached);
            Console.WriteLine("PASS HPIR live file Text: captured returns, a genuinely mixed result, gate-held dependencies, detached exact bytes and one sourceful failure");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void ContentChain(string root, string detached)
    {
        const string data = "staged 🧪\n";
        string target = Path.Combine(root, "target-link.txt");
        string referent = Path.Combine(root, "target-real.txt");
        File.WriteAllText(Path.Combine(root, "first.txt"), data);
        File.WriteAllText(referent, "original referent");
        if (OperatingSystem.IsWindows()) File.WriteAllText(target, "old target");
        else File.CreateSymbolicLink(target, referent);

        const string script = """
            let io = $std::io;
            let file = $std::files::File::new(./target-link.txt);
            fn forward(file) { $std::files::File::read_text(file) };
            ./first.txt -> ./target-link.txt;
            let contents = forward(file);
            contents -> ./observed.txt;
            io::println("admitted");
            io::print(contents);
            """;
        List<CompilationObservation> compiler = [];
        HpirProgram program = Compile(script, root, optimization: 0, new(compiler.Add, includeOptimizerPasses: true));
        if (!program.Imports.Any(import => import.Service == HpirService.LiveTransferFutureText))
            throw new Exception("File transfer did not retain its pending Text source.");
        if (!compiler.Any(item => item.Name == "compiler.hpir.select"
            && item.Metrics?.GetValueOrDefault("text.result_functions") >= 2
            && item.Metrics?.GetValueOrDefault("text.mixed_result_functions") == 0))
            throw new Exception("Definite Text completion forwarding should keep the single-pointer guest ABI.");
        var timeline = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var published = new ManualResetEventSlim();
        using var output = new StringWriter { NewLine = "\n" };
        _ = HpirExecutor.RunConfigured(program, new(WorkingDirectory: detached, Output: output) { Timeline = new(Record, "file-text") });
        if (output.ToString() != "admitted\n" + data || File.ReadAllText(Path.Combine(root, "observed.txt")) != data
            || File.ReadAllText(referent) != "original referent" || !timeline.Any(record => record.Name == "runtime.dependency"
                && record.Attributes.GetValueOrDefault("dependency.kind") == "transfer.text_input")
            || !timeline.Any(record => record.Name == "hpir.work.frame_exit"
                && record.Metrics.GetValueOrDefault("work.promoted") > 0)
            || !OperatingSystem.IsWindows() && !timeline.Any(record => record.Name == "hpir.file_read.admit"
                && record.Metrics.GetValueOrDefault("dependencies.live") == 1))
            throw new Exception("File Text did not order a replacing writer, survive shared demand, or preserve source alias behavior.");
        if (HpirMachineImage.IsSupported && Environment.GetEnvironmentVariable("MSYNC_JIT") == "force"
            && !timeline.Any(record => record.Name == "hpir.machine.publish" && record.Metrics.GetValueOrDefault("code.bytes") > 0))
            throw new Exception("Forced execution did not publish machine code for the artifact journey.");

        void Record(RuntimeTimelineRecord record)
        {
            timeline.Enqueue(record);
            if (!OperatingSystem.IsWindows() && record.Name == "file.sync" && !published.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The writer was blocked before publish; Future<Text> transfer was not independently admitted.");
            if (record.Name == "hpir.effect.admit"
                && record.Attributes.GetValueOrDefault("transfer.destination")?.EndsWith("observed.txt", StringComparison.Ordinal) == true)
                published.Set();
        }
    }

    private static void MixedResults(string root, string detached)
    {
        const string source = """
            fn read_or_ready(path: Path, read: Bool) -> String {
                if read { $std::io::read_text(path) } else { "ready literal" }
            };
            fn live(path: Path) -> String { $std::io::read_text(path) };
            fn literal(path: Path) -> String { "captured literal" };
            fn invoke_selected(path: Path, read: Bool) -> String {
                let selected = if read { live } else { literal };
                selected(path)
            };
            read_or_ready(./first.txt, exists ./first.txt) -> ./read-arm.txt;
            read_or_ready(./missing.txt, exists ./missing.txt) -> ./ready-arm.txt;
            invoke_selected(./first.txt, exists ./first.txt) -> ./read-call.txt;
            invoke_selected(./missing.txt, exists ./missing.txt) -> ./ready-call.txt;
            """;
        List<CompilationObservation> compiler = [];
        var program = Compile(source, root, optimization: 0, new(compiler.Add, includeOptimizerPasses: true));
        if (!compiler.Any(item => item.Name == "compiler.hpir.select"
            && item.Metrics?.GetValueOrDefault("text.mixed_result_functions") > 0)
            || !compiler.Any(item => item.Name == "compiler.hpir.select.function"
                && item.Metrics?.GetValueOrDefault("calls.finite_arms") >= 2
                && item.Metrics?.GetValueOrDefault("text.mixed_calls") >= 2)
            || !program.Imports.Any(import => import.Service == HpirService.LiveTransferFutureText)
            || !program.Imports.Any(import => import.Service == HpirService.LiveTransfer))
            throw new Exception("A genuinely mixed Text result must select both typed transfer carriers.");
        _ = HpirExecutor.RunConfigured(program, new(WorkingDirectory: detached));
        string actual = File.ReadAllText(Path.Combine(root, "first.txt"));
        if (File.ReadAllText(Path.Combine(root, "read-arm.txt")) != actual
            || File.ReadAllText(Path.Combine(root, "read-call.txt")) != actual
            || File.ReadAllText(Path.Combine(root, "ready-arm.txt")) != "ready literal"
            || File.ReadAllText(Path.Combine(root, "ready-call.txt")) != "captured literal")
            throw new Exception("The mixed result lost either the file completion or plain literal arm.");
    }

    private static void SourceFailure(string root, string detached)
    {
        HpirProgram missing = Compile("""
            fn missing(path: Path) -> String { $std::io::read_text(path) };
            missing(./missing.txt) -> ./not-produced.txt;
            """, root);
        var errors = new ConcurrentQueue<MsyncException>();
        var result = HpirExecutor.RunConfigured(missing,
            new(WorkingDirectory: detached) { ReportError = errors.Enqueue });
        if (result.FailedTransfers != 1 || errors.Count != 1 || !errors.TryPeek(out var error)
            || error.Span?.Source != Name || error.InnerException is not FileNotFoundException
            || File.Exists(Path.Combine(root, "not-produced.txt")))
            throw new Exception("A failed source read did not report once at its original source or wrote an output.");
    }

    private static HpirProgram Compile(string source, string root, int optimization = 2, CompilationTrace? trace = null)
    {
        var program = Compiler.LowerHpir(Compiler.Analyze(source, Name), optimization, trace);
        program = RoundTrip(program);
        program = HpirBinding.Prepare(program, new(root, root));
        return RoundTrip(program);

        static HpirProgram RoundTrip(HpirProgram program)
        {
            using var image = new MemoryStream();
            HpirArtifact.WriteProduced(program, image);
            image.Position = 0;
            return HpirArtifact.ReadTrusted(image);
        }
    }
}
