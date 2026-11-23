using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirFutureSourceChecks
{
    internal static void Run()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
        string directory = Path.Combine(root, ".temp", "hpir", "future-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                Check(level, directory);
                Check(level, directory, capture: true);
                CheckInput(level, directory);
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
                {
                    CheckFrameInput(level, directory, native);
                    CheckFrameDiscard(level, directory, native);
                }
            }
            CheckEliminatedProducer(directory);
            Console.WriteLine("PASS HPIR released process and typed input, detached Number/Bool demand and mixed SSA O0–O3");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckEliminatedProducer(string directory)
    {
        const string source = """
            let n = if 1 == 2 {
                let child = release /bin/sh("-c", "exit 19");
                child::exit_code
            } else { 7 };
            $std::io::println((n + 1) as String)
            """;
        List<CompilationObservation> observations = [];
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        var module = Compiler.Analyze(source, Path.Combine(directory, "elided-process.msync"), trace);
        var portable = Compiler.LowerHpir(module, 2, trace);
        Require(observations.Any(item => item.Metrics?.GetValueOrDefault("scalar.former_lifts_ready") == 1)
            && portable.Imports.All(item => item.Service is not
                (HpirService.ProcessStart or HpirService.FutureLift)),
            "LLVM can eliminate the pending producer after upstream readiness; its surviving raw operation must stay primitive.");
        using var output = new StringWriter { NewLine = "\n" };
        var program = HpirBinding.Prepare(portable, new(directory, directory));
        _ = HpirExecutor.Run(program, new(Output: output));
        Require(output.ToString() == "8\n", "A surviving formerly lifted operation was not the ordinary Number add.");
    }

    private static void CheckFrameInput(int level, string directory, bool native)
    {
        const string source = """
            fn next_is_null() -> Bool {
                let line? = $std::io::read_line();
                line is null
            };
            fn relay() -> Bool { next_is_null() };
            let empty = relay();
            $std::io::println("queued");
            if empty { 7 } else { 3 }
            """;
        var module = Compiler.Analyze(source, Path.Combine(directory, "child-input.msync"));
        var artifact = HpirBinding.Prepare(Compiler.LowerHpir(module, level), new(directory, directory));
        var output = new QueuedWriter();
        using var reader = new GateReader();
        var events = new System.Collections.Concurrent.ConcurrentQueue<RuntimeTimelineRecord>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var machine = native ? HpirMachineImage.Compile(artifact) : null;
        var options = new ExecutionOptions(WorkingDirectory: directory, Output: output)
            { Input = reader, Timeline = new(events.Enqueue, "child-input") };
        var run = Task.Run(() => machine is null ? HpirExecutor.Run(artifact, options, stop.Token)
            : HpirExecutor.RunMachineCandidate(machine, options, stop.Token));
        try
        {
            output.Queued.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(!run.IsCompleted && events.Count(item => item.Name == "hpir.work.frame_exit"
                && item.Metrics.GetValueOrDefault("work.promoted") == 1) == 2,
                "Returning a pending Bool must promote the owning host read without demanding it.");
            reader.Line.SetResult(null);
            Require(BitConverter.UInt64BitsToDouble(run.WaitAsync(stop.Token).GetAwaiter().GetResult()) == 7,
                "Promoted input completion did not survive the child frame.");
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
    }

    private static void CheckFrameDiscard(int level, string directory, bool native)
    {
        const string source = """
            fn forget() -> Number {
                $std::io::read_line();
                5
            };
            forget()
            """;
        var module = Compiler.Analyze(source, Path.Combine(directory, "discarded-input.msync"));
        var artifact = HpirBinding.Prepare(Compiler.LowerHpir(module, level), new(directory, directory));
        using var reader = new GateReader();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var events = new System.Collections.Concurrent.ConcurrentQueue<RuntimeTimelineRecord>();
        using var machine = native ? HpirMachineImage.Compile(artifact) : null;
        var options = new ExecutionOptions(WorkingDirectory: directory)
            { Input = reader, Timeline = new(events.Enqueue, "discarded-input") };
        ulong result = machine is null ? HpirExecutor.Run(artifact, options, stop.Token)
            : HpirExecutor.RunMachineCandidate(machine, options, stop.Token);
        Require(BitConverter.UInt64BitsToDouble(result) == 5 && !reader.Line.Task.IsCompleted
            && events.Any(item => item.Name == "hpir.work.frame_exit"
                && item.Metrics.GetValueOrDefault("work.discarded") == 1
                && item.Metrics.GetValueOrDefault("work.joined") == 1),
            "A child that returns no completion must cancel and settle its own unread input.");
    }

    private static void Check(int optimization, string directory, bool capture = false)
    {
        string source = """
            fn increment(n: Number) -> Number {
                $std::math::floor($std::math::fma(n, 1, 1))
            };
            let child = release /bin/sh("-c", "echo one >> started; while [ ! -f go ]; do sleep 0.01; done; exit 7");
            let result = increment(child::exit_code);
            let result_ok = result == 8;
            $std::io::println("queued");
            let selected = if result_ok { result } else { 0 };
            increment(selected)
            """;
        if (capture)
            source = source.Replace("let result = increment(child::exit_code);", """
                let state = child::exit_code;
                fn update_state() -> Number {
                    global state;
                    state = increment(state);
                    state
                };
                let result = update_state();
                """, StringComparison.Ordinal);
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        string sourceFile = Path.Combine(directory, "program.msync");
        File.WriteAllText(sourceFile, source);
        var program = Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(sourceFile), sourceFile, trace), optimization, trace);
        var services = program.Imports.Select(import => import.Service).ToHashSet();
        Require(services.IsSupersetOf([HpirService.ProcessStart, HpirService.ProcessExit,
            HpirService.FutureLift, HpirService.FutureDemand, HpirService.FutureMath]),
            $"Missing typed producer operations at O{optimization}: {string.Join(", ", services)}");
        File.Delete(sourceFile);
        var detached = Reload(program);
        var artifact = Reload(HpirBinding.Prepare(detached, new(directory, directory), trace: trace));
        var output = new QueuedWriter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        File.Delete(Path.Combine(directory, "go"));
        File.Delete(Path.Combine(directory, "started"));
        Task<ulong> run = Task.Run(() => HpirExecutor.Run(artifact, new(WorkingDirectory: directory, Output: output), stop.Token));
        try
        {
            output.Queued.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(!run.IsCompleted, "Lift forced a future or skipped Bool demand.");
            File.WriteAllText(Path.Combine(directory, "go"), "go");
            ulong result = run.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(BitConverter.UInt64BitsToDouble(result) == 9, "Typed process payload or mixed control was lost.");
            Require(File.ReadAllText(Path.Combine(directory, "started")) == "one\n", "The two demands started more than one child.");
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
        Require(observations.Any(observation => observation.Name == "compiler.hpir.select.function"
            && observation.Metrics?.GetValueOrDefault("future.known_cells") >= 1
            && observation.Metrics.GetValueOrDefault("future.mixed_cells") >= 2),
            "Known completion roots are absent from compiler attribution.");
        if (capture) Require(observations.Any(observation => observation.Name == "compiler.llvm.lower.readiness"
            && observation.Metrics?.GetValueOrDefault("flow.future_captures") > 0),
            "A mixed captured location needs a producer-owned storage contract.");
        return;

        HpirProgram Reload(HpirProgram image)
        {
            using var stream = new MemoryStream();
            HpirArtifact.WriteProduced(image, stream, trace);
            stream.Position = 0;
            return HpirArtifact.ReadTrusted(stream, trace);
        }
    }

    private static void CheckInput(int level, string directory)
    {
        const string source = """
            let line? = $std::io::read_line();
            $std::io::println("queued");
            let selected = if line is null { 0 } else { line as Number };
            selected + 1
            """;
        var module = Compiler.Analyze(source, Path.Combine(directory, "input.msync"));
        HpirProgram program = Compiler.LowerHpir(module, level);
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        var artifact = HpirBinding.Prepare(HpirArtifact.ReadTrusted(stream), new(directory, directory));
        RunLine("7", 8);
        RunLine(null, 1);
        return;

        void RunLine(string? line, double expected)
        {
            var output = new QueuedWriter();
            using var reader = new GateReader();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var run = Task.Run(() => HpirExecutor.Run(artifact, new(WorkingDirectory: directory, Output: output)
                { Input = reader }, stop.Token));
            try
            {
                output.Queued.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
                Require(!run.IsCompleted, "A typed input must not block early or finish without the selected post-marker demand.");
                reader.Line.SetResult(line);
                Require(BitConverter.UInt64BitsToDouble(run.WaitAsync(stop.Token).GetAwaiter().GetResult()) == expected,
                    $"Wrong nullable input or mixed scalar payload at O{level}.");
            }
            finally
            {
                stop.Cancel();
                if (!run.IsCompleted)
                    try { _ = run.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
            }
        }
    }

    private sealed class GateReader : TextReader, IAsyncLineInput
    {
        internal TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) => new(Line.Task.WaitAsync(token));
    }

    private sealed class QueuedWriter : StringWriter
    {
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            Require(buffer.Span.SequenceEqual("queued"), "Unexpected guest output.");
            Queued.SetResult();
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
