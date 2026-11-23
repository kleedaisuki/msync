using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirCooperativeChecks
{
    private const string InputSource = """
        let record = scope { value: 37 };
        fn read(n: Number) -> Number {
            let line? = $std::io::read_line();
            if line? { record::value + n } else { 1 }
        };
        fn churn(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                let item = scope { value: i };
                i = item::value + 1;
            }
        };
        let values = $std::ranges::transform($std::ranges::iota(1, 3), read);
        let first = $std::ranges::front(values);
        let consumed = churn(35000);
        $std::io::println("queued");
        $std::io::println((first + consumed - 35000) as String);
        """;

    private const string ProcessSource = """
        fn transform(n: Number) -> Number {
            let child = release /bin/sh("-c", "while [ ! -f continue ]; do sleep 0.01; done; exit 3");
            if child::success { 42 } else { 7 }
        };
        let view = $std::ranges::transform($std::ranges::iota(0, 8), transform);
        let front = $std::ranges::front(view);
        $std::io::println("queued");
        $std::io::println(front as String);
        """;

    internal static void Run()
    {
        string path = Path.GetFullPath(Path.Combine(".temp", "hpir", "cooperative", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
                {
                    var input = Prepare(InputSource, path, level);
                    using var reader = new Input();
                    RunGated(input, path, "queued\n38\n", native, reader, () => reader.Line.SetResult(null), expectCollection: true);
                    Require(reader.Reads == 1, "a future demand replayed its input");
                    if (OperatingSystem.IsWindows()) continue;
                    File.Delete(Path.Combine(path, "continue"));
                    var process = Prepare(ProcessSource, path, level);
                    RunGated(process, path, "queued\n7\n", native, null,
                        () => File.WriteAllText(Path.Combine(path, "continue"), "go"), expectCollection: false);
                }
            }
            CheckEmpty(path);
            CheckDiscard(path);
            if (!OperatingSystem.IsWindows()) CheckCaptured(path);
        }
        finally { Directory.Delete(path, recursive: true); }
        Console.WriteLine("PASS cooperative HPIR Number front: parent progress, shared roots, captures, delayed failure and discard");
    }

    private static HpirProgram Prepare(string source, string path, int level)
    {
        string filename = Path.Combine(path, "cooperative.msync");
        File.WriteAllText(filename, source);
        HpirProgram obj = Reload(Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(filename), filename), level));
        File.Delete(filename);
        var bound = Reload(HpirBinding.Prepare(obj, new(path, path)));
        Require(bound.Imports.Any(import => import.Service == HpirService.GuestNumber),
            "selected scalar front did not preserve its producer-owned suspension");
        return bound;
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using MemoryStream image = new();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private static void RunGated(HpirProgram program, string path, string expected, bool native,
        TextReader? input, Action release, bool expectCollection)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output = new MarkerWriter { NewLine = "\n" };
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: path, Output: output)
        {
            Input = input, Timeline = new(events.Enqueue, "cooperative")
        };
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        var run = Task.Run(() => machine is null ? HpirExecutor.Run(program, options, stop.Token)
            : HpirExecutor.RunMachineCandidate(machine, options, stop.Token));
        try
        {
            output.Marker.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(!run.IsCompleted && events.Count(item => item.Name == "hpir.guest.slice"
                && item.Attributes.GetValueOrDefault("guest.state") == "parked") == 1,
                "parent did not reach its independent output while the child was actually parked");
            release();
            _ = run.WaitAsync(stop.Token).GetAwaiter().GetResult();
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
        Require(output.ToString() == expected && events.Count(item => item.Name == "hpir.guest.launch") == 1
            && events.Count(item => item.Name == "hpir.guest.resume") == 1, "the original child did not resume exactly once");
        if (expectCollection) Require(events.Any(item => item.Name == "hpir.heap.collect"
            && item.Metrics.GetValueOrDefault("roots.cooperative_frames") > 0), "no collection scanned the parked child");
    }

    private static void CheckEmpty(string path)
    {
        HpirProgram program = Prepare(InputSource.Replace("iota(1, 3)", "iota(1, 1)", StringComparison.Ordinal), path, 2);
        using var output = new MarkerWriter { NewLine = "\n" };
        using var input = new Input();
        try
        {
            _ = HpirExecutor.Run(program, new(Output: output) { Input = input });
            throw new InvalidOperationException("an empty cooperative Range front succeeded");
        }
        catch (MsyncException error) when (error.Message == "ranges.front requires a nonempty Range" &&
            error.Span?.Source == Path.Combine(path, "cooperative.msync"))
        {
            Require(input.Reads == 0 && output.ToString() == "queued\n", "empty selected the callback or failed before demand");
        }
    }

    private static void CheckDiscard(string path)
    {
        string source = InputSource.Replace("$std::io::println((first + consumed - 35000) as String);", "", StringComparison.Ordinal);
        HpirProgram program = Prepare(source, path, 2);
        using var output = new MarkerWriter { NewLine = "\n" };
        using var input = new Input();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var events = new List<RuntimeTimelineRecord>();
        _ = HpirExecutor.Run(program, new(Output: output) { Input = input, Timeline = new(events.Add, "cooperative-discard") }, stop.Token);
        Require(output.ToString() == "queued\n" && input.Reads == 1 && !input.Line.Task.IsCompleted
            && events.Any(item => item.Name == "hpir.guest.settle" && item.Metrics.GetValueOrDefault("guest.continuations_stopped") == 1)
            && !events.Any(item => item.Name == "hpir.guest.resume"), "discard did not close the parked continuation");
    }

    private static void CheckCaptured(string path)
    {
        const string source = """
            let child = release /bin/sh("-c", "while [ ! -f continue ]; do sleep 0.01; done; exit 3");
            let passed = child::success;
            fn transform(n: Number) -> Number { if passed { 42 } else { 7 } };
            let view = $std::ranges::transform($std::ranges::iota(0, 8), transform);
            let front = $std::ranges::front(view);
            $std::io::println("queued");
            $std::io::println(front as String);
            """;
        File.Delete(Path.Combine(path, "continue"));
        HpirProgram program = Prepare(source, path, 2);
        RunGated(program, path, "queued\n7\n", HpirMachineImage.IsSupported, null,
            () => File.WriteAllText(Path.Combine(path, "continue"), "go"), expectCollection: false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("HPIR cooperative Range: " + message);
    }

    private sealed class Input : TextReader, IAsyncLineInput
    {
        internal TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        {
            ++Reads;
            return new(Line.Task.WaitAsync(token));
        }
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal TaskCompletionSource Marker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            WriteLine(text.Span);
            if (text.Span.SequenceEqual("queued")) Marker.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
