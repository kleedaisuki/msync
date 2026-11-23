using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirProcessSourceChecks
{
    private const string Projections = """
        fn launch() -> Process {
            release /bin/sh("-c", "echo one >> started; while [ ! -f go ]; do sleep 0.01; done; printf typed; printf diagnostic >&2; exit 7")
        };
        fn status(child: Process) -> Number { child::exit_code };
        let child = launch();
        let code = status(child);
        let output = child::stdout;
        let error = child::stderr;
        $std::io::println("queued");
        $std::io::println(output);
        $std::io::eprintln(error);
        code
        """;

    private const string EscapingProjection = """
        fn launch() -> Number {
            let child = release /bin/sh("-c", "echo one >> started; while [ ! -f go ]; do sleep 0.01; done; exit 7");
            child::exit_code + 1
        };
        let code = launch();
        $std::io::println("queued");
        code
        """;

    private const string Private = """
        fn launch() -> Number {
            release /bin/sh("-c", "echo one >> started; while [ ! -f go ]; do sleep 0.01; done; exit 7");
            5
        };
        let code = launch();
        $std::io::println("queued");
        code
        """;

    private const string Collection = """
        fn churn(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                let text = i as String;
                i = (text as Number) + 1;
            }
        };
        let child = release /bin/sh("-c", "printf retained; exit 7");
        let output = child::stdout;
        if child::exit_code != 7 { return 0; };
        let before = churn(14000);
        $std::io::println(output);
        before + churn(14000)
        """;

    internal static void Run()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
        string directory = Path.Combine(root, ".temp", "hpir", "process-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                Check(Projections, 7, level, directory, text: true, escapes: true);
                Check(EscapingProjection, 8, level, directory, text: false, escapes: true);
                Check(Private, 5, level, directory, text: false, escapes: false);
                CheckOwnedProcess(directory, level);
            }
            CheckDynamicExecutable(directory);
            CheckCollection(directory);
            Console.WriteLine("PASS released Process through fixed call/return, typed stdout/stderr, escaping projection and private work join at O0–O3");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckOwnedProcess(string directory, int level)
    {
        const string source = """"
            let process = $std::process::spawn("/bin/sh", ["-c", "printf '%s:' ""$$""; cat"]);
            let identity = $std::process::pid(process);
            let running = $std::process::running(process);
            $std::process::write_stdin(process, "Klee 星🙂");
            $std::process::close_stdin(process);
            let completed = $std::process::wait(process);
            let expected = $std::string::concat(identity as String, ":Klee 星🙂");
            let output_matches = $std::process::stdout(completed) == expected;
            let stopped = $std::process::spawn(/bin/sh, ["-c", "IFS= read -r line"]);
            let requested = $std::process::kill(stopped);
            let killed = $std::process::wait(stopped);
            running and output_matches and requested and not $std::process::success(killed)
            """";
        string file = Path.Combine(directory, "owned-process.msync");
        HpirProgram image = Reload(HpirBinding.Prepare(Reload(Compiler.LowerHpir(Compiler.Analyze(source, file), level)),
            new(directory, directory)));
        Require(image.Imports.Any(import => import.Service == HpirService.ProcessTextReady)
            && !image.Imports.Any(import => import.Service is HpirService.ProcessStdout or HpirService.DemandText),
            "An explicit process.wait must leave stdout in its already ready Text representation.");
        Verify(null);
        if (HpirMachineImage.IsSupported)
        {
            using var machine = HpirMachineImage.Compile(image);
            Verify(machine);
        }
        return;

        void Verify(HpirMachineImage? machine)
        {
            var events = new ConcurrentQueue<RuntimeTimelineRecord>();
            var options = new ExecutionOptions { Timeline = new(events.Enqueue, "owned-process") };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ulong result = machine is null ? HpirExecutor.Run(image, options, stop.Token)
                : HpirExecutor.RunMachineCandidate(machine, options, stop.Token);
            Require(result == 1, "Process control must observe the same live pipe/identity, then wait for the actual child.");
            Require(events.Count(item => item.Name == "hpir.process.start_wait") == 2
                && events.Any(item => item.Name == "hpir.process.control" && item.Attributes.GetValueOrDefault("process.wait_for") == "completion"),
                "Process admission and completion must be distinct attributed operations.");
        }
    }

    private static void CheckDynamicExecutable(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        const string source = """
            fn command(directory: Path, name: String) -> Path { directory / name };
            fn run(executable: Path, data: Path, value: Number, flag: Bool) -> Process {
                release executable("typed argument", data, value, flag)
            };
            let child = run(command(./bin, "runner.sh"), ./data.txt, 12.5, false);
            $std::io::println(child::stdout);
            child::exit_code
            """;
        string bin = Directory.CreateDirectory(Path.Combine(directory, "bin")).FullName;
        string elsewhere = Directory.CreateDirectory(Path.Combine(directory, "elsewhere")).FullName;
        string executable = Path.Combine(bin, "runner.sh");
        File.WriteAllText(executable, "#!/bin/sh\nprintf '%s|%s|%s|%s|%s' \"$PWD\" \"$1\" \"$2\" \"$3\" \"$4\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var module = Compiler.Analyze(source, Path.Combine(directory, "dynamic-executable.msync"));
        var program = Reload(HpirBinding.Prepare(Reload(Compiler.LowerHpir(module, 2)), new(directory, directory)));
        HpirImport launch = program.Imports.Single(import => import.Service == HpirService.ProcessStart);
        Require(launch.ProcessArguments is [HpirProcessArgument.Text, HpirProcessArgument.Path,
                HpirProcessArgument.Number, HpirProcessArgument.Bool]
            && launch.Arguments is [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64, HpirWidth.U64, HpirWidth.U8],
            "A mixed Process argv lost its producer-selected carriers in the bound image.");
        Check(null);
        if (HpirMachineImage.IsSupported)
        {
            using var machine = HpirMachineImage.Compile(program);
            Check(machine);
        }

        void Check(HpirMachineImage? image)
        {
            using StringWriter output = new() { NewLine = "\n" };
            var options = new ExecutionOptions(WorkingDirectory: elsewhere, Output: output);
            ulong result = image is null ? HpirExecutor.Run(program, options) : HpirExecutor.RunMachineCandidate(image, options);
            Require(result == 0 && output.ToString() == $"{directory}|typed argument|{directory}/./data.txt|12.5|false\n",
                $"Wrong detached Process context or arguments: {output}");
        }
    }

    private static void CheckCollection(string directory)
    {
        var module = Compiler.Analyze(Collection, Path.Combine(directory, "collect.msync"));
        HpirProgram program = Reload(HpirBinding.Prepare(Reload(Compiler.LowerHpir(module, 2)), new(directory, directory)));
        using var output = new StringWriter { NewLine = "\n" };
        var events = new List<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: directory, Output: output)
        { Timeline = new(events.Add, "process-reachability") };
        Require(BitConverter.UInt64BitsToDouble(HpirExecutor.Run(program, options)) == 28000
            && output.ToString() == "retained\n"
            && events.Aggregate(0UL, (count, item) => count + item.Metrics.GetValueOrDefault("gc.process_results_released")) == 1,
            "A live Text projection must keep its Process across GC, then let the completed process result be reclaimed.");
    }

    private static void Check(string source, double expected, int level, string directory, bool text, bool escapes)
    {
        HpirProgram produced = Compiler.LowerHpir(
            Compiler.Analyze(source, Path.Combine(directory, "release.msync")), level);
        if (text)
            Require(produced.Imports.Any(import => import.Service == HpirService.ProcessStdout)
                && produced.Imports.Any(import => import.Service == HpirService.ProcessStderr),
                "The two text projections must be independent typed consumers of one actual process.");
        HpirProgram program = Reload(HpirBinding.Prepare(Reload(produced), new(directory, directory)));
        Run(null);
        if (HpirMachineImage.IsSupported)
        {
            using var image = HpirMachineImage.Compile(program);
            Run(image);
        }
        return;

        void Run(HpirMachineImage? machine)
        {
            string release = Path.Combine(directory, "go"), starts = Path.Combine(directory, "started");
            File.Delete(release);
            File.Delete(starts);
            var events = new ConcurrentQueue<RuntimeTimelineRecord>();
            using var output = new MarkerWriter();
            using var error = new StringWriter { NewLine = "\n" };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = new ExecutionOptions(WorkingDirectory: directory, Output: output, Error: error)
            { Timeline = new(events.Enqueue, "process-result") };
            Task<ulong> result = Task.Run(() => machine is null
                ? HpirExecutor.Run(program, options, stop.Token)
                : HpirExecutor.RunMachineCandidate(machine, options, stop.Token));
            try
            {
                if (escapes)
                {
                    output.Emitted.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
                    Require(!result.IsCompleted && events.Any(item => item.Name == "hpir.work.frame_exit"
                        && item.Metrics.GetValueOrDefault("work.promoted") == 1),
                        "The reachable Process must leave the child without awaiting it.");
                }
                else
                {
                    while (!File.Exists(starts) && !result.IsCompleted)
                        Task.Delay(5, stop.Token).GetAwaiter().GetResult();
                    Require(!output.Emitted.Task.IsCompleted, "An unobserved, private process outlived its owning function.");
                }
                File.WriteAllText(release, "go");
                Require(BitConverter.UInt64BitsToDouble(result.WaitAsync(stop.Token).GetAwaiter().GetResult()) == expected
                    && File.ReadAllText(starts) == "one\n", "Wrong result, or a projection started the process again.");
                Require(output.ToString() == (text ? "queued\ntyped\n" : "queued\n")
                    && error.ToString() == (text ? "diagnostic\n" : ""), "The two streams lost their individual bytes or order.");
                if (!escapes)
                    Require(events.Any(item => item.Name == "hpir.work.frame_exit"
                        && item.Metrics.GetValueOrDefault("work.joined") == 1), "The frame did not join the private child.");
            }
            finally
            {
                stop.Cancel();
                if (!result.IsCompleted)
                    try { _ = result.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
            }
        }
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal TaskCompletionSource Emitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            WriteLine(buffer.Span);
            if (buffer.Span.SequenceEqual("queued")) Emitted.SetResult();
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
