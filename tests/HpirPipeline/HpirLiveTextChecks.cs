using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirLiveTextChecks
{
    private const string Name = "hpir-live-lines.msync";
    private const string BreakSource = """
        let io = $std::io;
        let inert = io::stdin_lines();
        io::println("constructed");
        for line in io::stdin_lines() {
            io::println(line);
            break;
        };
        """;

    internal static void Run()
    {
        string current = Environment.CurrentDirectory;
        for (int level = 0; level <= 3; level++)
        {
            HpirProgram program = Compile(BreakSource, level, current);
            Gated(program, cancel: false);
        }
        Gated(Compile(BreakSource, 2, current), cancel: true);
        Exhaustion(current);
        Files(Path.Combine(current, ".temp", "hpir", $"live-text-{Guid.NewGuid():N}"));
        Console.WriteLine("PASS HPIR live Text Range: lazy stdin/file, detached live bytes, EOF, break, failure and cancellation");
    }

    internal static void RunJoins()
    {
        const string source = """
            let io = $std::io;
            let lines = io::stdin_lines();
            io::println("constructed");
            io::println($std::string::join(lines, "🧪"));
            """;
        string current = Environment.CurrentDirectory;
        HpirProgram joined = Compile(source, 2, current);
        if (!joined.Imports.Any(import => import.Service == HpirService.TextRangeJoin)
            || joined.Imports.Any(import => import.Service == HpirService.TextRangeNext))
            throw new Exception("Join did not select the direct cursor terminal.");
        Gated(joined, cancel: false, join: true);
        Gated(joined, cancel: true, join: true);

        using var output = new StringWriter { NewLine = "\n" };
        using var input = new StringReader("a\r\n\r\nb\r\n");
        var profile = new RuntimeProfile();
        _ = HpirExecutor.Run(joined, new(Output: output) { Input = input, Profile = profile });
        var counts = profile.Drain();
        if (output.ToString() != "constructed\na🧪🧪b\n" || Count(counts, "text_join_items") != 3
            || Count(counts, "text_range_exhaustions") != 1)
            throw new Exception("Join confused a blank line with EOF or misplaced the delimiter.");

        string missing = Directory.CreateDirectory(Path.Combine(current, ".temp", "hpir", $"join-missing-{Guid.NewGuid():N}")).FullName;
        try
        {
            HpirProgram invalid = Compile("$std::io::print($std::string::join($std::io::read_lines(./missing), \"|\"));", 2, missing);
            try { _ = HpirExecutor.Run(invalid); throw new Exception("A demanded missing source should fail."); }
            catch (MsyncException error) when (error.Span?.Source == Name && error.InnerException is FileNotFoundException) { }
        }
        finally { Directory.Delete(missing, true); }
        Console.WriteLine("PASS HPIR live Text join: first-pull, blank and EOF, cancellation and sourceful error");
    }

    private static void Gated(HpirProgram program, bool cancel, bool join = false)
    {
        using var input = new GatedReader();
        using var output = new StringWriter { NewLine = "\n" };
        using var stop = new CancellationTokenSource();
        var timeline = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(Output: output) { Input = input, Timeline = new(timeline.Enqueue, "live-text") };
        Task<ulong> run = Task.Run(() => HpirExecutor.Run(program, options, stop.Token));
        try
        {
            Task first = Task.WhenAny(input.Started.Task, run).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (first == run) _ = run.GetAwaiter().GetResult();
            if (first != input.Started.Task || run.IsCompleted || output.ToString() != "constructed\n" || input.Reads != 1)
                throw new Exception("Constructing a live Range read from stdin or failed to preserve the marker before demand.");
        }
        finally
        {
            if (cancel) stop.Cancel();
            else input.Line.TrySetResult("🧪");
        }
        try
        {
            _ = run.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (cancel) throw new Exception("Live Text Range ignored input cancellation.");
        }
        catch (OperationCanceledException) when (cancel) { }

        if (input.Reads != (join && !cancel ? 2 : 1) || input.Disposed
            || output.ToString() != (cancel ? "constructed\n" : "constructed\n🧪\n"))
            throw new Exception("Live stdin prefetched across break or disposed its borrowed input.");
        RequireClose(timeline, cancel ? "unwind" : join ? "exhausted" : "explicit");
        var pull = timeline.First(item => item.Name == "hpir.text_range.next");
        if (pull.Attributes.GetValueOrDefault("source.file") != Name
            || pull.Outcome != (cancel ? "cancelled" : "ok"))
            throw new Exception("Live Text Range did not attribute the actual pending read.");
    }

    private static void Exhaustion(string current)
    {
        HpirProgram program = Compile("for line in $std::io::stdin_lines() { $std::io::println(line); };", 2, current);
        using var input = new StringReader("first\r\n\r\n");
        using var output = new StringWriter { NewLine = "\n" };
        var profile = new RuntimeProfile();
        var timeline = new ConcurrentQueue<RuntimeTimelineRecord>();
        _ = HpirExecutor.Run(program, new(Output: output) { Input = input, Profile = profile, Timeline = new(timeline.Enqueue, "eof") });
        var samples = profile.Drain();
        if (output.ToString() != "first\n\n" || Count(samples, "text_range_yields") != 2
            || Count(samples, "text_range_exhaustions") != 1 || Count(samples, "text_range_code_units") != 5)
            throw new Exception("Blank text was not distinguishable from EOF, or pull counts were incorrect.");
        RequireClose(timeline, "exhausted");
    }

    private static void Files(string directory)
    {
        Directory.CreateDirectory(directory);
        string other = Path.Combine(directory, "elsewhere");
        Directory.CreateDirectory(other);
        try
        {
            string file = Path.Combine(directory, "live.txt");
            File.WriteAllText(file, "stale\n");
            const string source = """
                let io = $std::io;
                let unopened = io::read_lines(./not-there.txt);
                for line in io::read_lines(./live.txt) {
                    io::println(line);
                    break;
                };
                """;
            HpirProgram executable = Compile(source, 2, directory);
            File.WriteAllText(file, "live-🧪\nuncopied\n");
            using var output = new StringWriter { NewLine = "\n" };
            var timeline = new ConcurrentQueue<RuntimeTimelineRecord>();
            var options = new ExecutionOptions(WorkingDirectory: other, Output: output) { Timeline = new(timeline.Enqueue, "file") };
            _ = HpirExecutor.Run(executable, options);
            if (output.ToString() != "live-🧪\n") throw new Exception("ReadLines ignored the prepared anchors or read stale bytes.");
            RequireClose(timeline, "explicit");

            const string failure = """
                for line in $std::io::read_lines("./live.txt") {
                    $std::string::substring(line, 99, 1);
                };
                """;
            HpirProgram invalid = Compile(failure, 2, directory);
            timeline.Clear();
            try { _ = HpirExecutor.Run(invalid, options); throw new Exception("Expected the invalid substring operation to fail."); }
            catch (MsyncException error) when (error.Message.Contains("Unicode scalar values", StringComparison.Ordinal)) { }
            RequireClose(timeline, "unwind");
            using (FileStream exclusive = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            const string ordered = """
                "fresh" -> ./ordered.txt;
                for line in $std::io::read_lines(./ordered.txt) { $std::io::println(line); };
                """;
            File.WriteAllText(Path.Combine(directory, "ordered.txt"), "stale\n");
            HpirProgram transferAndRead = Compile(ordered, 2, directory);
            if (!transferAndRead.Imports.Any(import => import.Service == HpirService.LiveTransfer))
                throw new Exception("The transfer/read control did not use the live resource frontier.");
            output.GetStringBuilder().Clear();
            timeline.Clear();
            _ = HpirExecutor.Run(transferAndRead, options);
            if (output.ToString() != "fresh\n" || !timeline.Any(item => item.Name == "hpir.effect.read_dependencies"
                && item.Attributes["input.resource"].EndsWith("ordered.txt", StringComparison.Ordinal)))
                throw new Exception("First file demand ignored a preceding live writer.");
            RequireClose(timeline, "exhausted");

            HpirProgram missing = Compile("for line in $std::io::read_lines(./not-there.txt) { $std::io::println(line); };", 2, directory);
            try
            {
                _ = HpirExecutor.Run(missing, options with { Timeline = null });
                throw new Exception("Reading a missing file should fail when pulled.");
            }
            catch (MsyncException error) when (error.Span?.Source == Name && error.InnerException is FileNotFoundException
                && error.Message.Contains("not-there.txt", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RequireClose(IEnumerable<RuntimeTimelineRecord> timeline, string reason)
    {
        RuntimeTimelineRecord close = timeline.Single(item => item.Name == "hpir.text_range.close");
        if (close.Outcome != "ok" || close.Attributes.GetValueOrDefault("cleanup.reason") != reason)
            throw new Exception($"Live Text Range did not close exactly once for {reason}.");
    }

    private static ulong Count(IEnumerable<RuntimeProfileSample> samples, string key) =>
        samples.Aggregate(0UL, (total, item) => total + item.Metrics.GetValueOrDefault(key));

    private static HpirProgram Compile(string source, int level, string directory)
    {
        HpirProgram portable = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, Name), level));
        return RoundTrip(HpirBinding.Prepare(portable, new(directory, directory)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private sealed class GatedReader : TextReader, IAsyncLineInput
    {
        internal readonly TaskCompletionSource<string?> Line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        internal bool Disposed;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (Reads++ != 0) return ValueTask.FromResult<string?>(null);
            Started.TrySetResult();
            return new(Line.Task.WaitAsync(cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = disposing;
            base.Dispose(disposing);
        }
    }
}
