using System.Diagnostics;
using System.Globalization;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class Program
{
    private static readonly string[] Commands = ["run", "check", "compile", "emit-ir", "bind-ir", "run-ir", "plan", "plan-ir", "fmt", "repl", "doctor", "config"];
    private const string Help = """
        msync 0.8.0 — a typed file synchronization language

        USAGE
          msync [run] FILE              Execute .msync, .mso, or .mexe ('-' is stdin)
          msync check FILE              Infer types without running effects
          msync compile FILE -o FILE    Emit binary relocatable .mso bytecode
          msync emit-ir FILE -o FILE    Emit relocatable bytecode
          msync bind-ir FILE -o FILE    Bind bytecode to this context as .mexe
          msync run-ir FILE             Run .mso or .mexe without source
          msync plan FILE               Inspect typed functions and basic blocks
          msync fmt FILE                Format source indentation
          msync repl                    Start an interactive session
          msync config                  Print effective TOML settings and origins
          msync doctor                  Inspect optional native transports
          msync legacy ...              Run the separately installed Rust baseline
          msync --language-server       Serve LSP on standard input/output

        OPTIONS
          -o, --output FILE             Artifact output ('-' is binary stdout)
          -n, --dry-run                 Show execution without filesystem changes
          -O0, -O1, -O2                 Machine-independent optimization level
          --source-file FILE            Explicit source identity for binding
          --jobs N                      Runtime copy/process concurrency
          --buffer-size N               Transfer buffer size (for example 64KiB)
          --memory-limit N              Aggregate payload-buffer budget
          --spool-limit N               Process output limit, or unlimited
          --check                       Check formatting without changing it
          --stdout                      Print formatted source
          --syntax 0.8                  Select the current grammar
          --color auto|always|never      Select interactive color policy
          --config FILE                 Read a specific TOML configuration
          --no-config                   Disable configuration discovery
          --trace FILE                  Write opt-in JSONL telemetry ('-' is stderr)
          --telemetry-db FILE           Write opt-in SQLite telemetry
          --no-telemetry                Disable configured telemetry
          -h, --help                    Show help
          -V, --version                 Show version

        Braces delimit expressions; ';' discards a value. A block's last
        unterminated expression is its result. Old source/artifacts are executed
        only by the preserved Rust implementation, never reinterpreted as 0.8.
        """;

    public static async Task<int> Main(string[] args)
    {
        TelemetrySession? telemetry = null;
        try
        {
            if (args.Length != 0 && args[0] == "legacy") return await Legacy(args[1..]);
            if (args.Contains("--language-server", StringComparer.Ordinal)) return await new LanguageServer(CheckForEditor, source => Formatter.Format(source)).RunAsync();
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal)) { Console.WriteLine(Help); return 0; }
            if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-V", StringComparer.Ordinal)) { Console.WriteLine("msync 0.8.0"); return 0; }
            Options options = Options.Parse(args);
            Configuration configuration = Configuration.Load(options.ConfigPath, options.NoConfig, options.Overrides);
            if (options.Command == "config") { configuration.Dump(Console.Out); return 0; }
            configuration.Validate();
            options = options with
            {
                Jobs = configuration.Jobs,
                Syntax = configuration.Syntax,
                BufferSize = configuration.BufferSize,
                MemoryLimit = checked((long)configuration.MemoryLimit),
                SpoolLimit = configuration.SpoolLimit is { } limit ? checked((long)limit) : null
            };
            if (options.Syntax is not (null or "0.8" or "0.8.0")) return await Legacy(args);
            telemetry = TelemetrySession.TryOpen(configuration, options.Command, options.File, options.TracePath, options.TelemetryDatabase, options.NoTelemetry);
            if (options.Command == "repl") return Complete(await Repl(options, telemetry, configuration), telemetry);
            if (options.Command == "doctor") return Complete(Doctor(), telemetry);
            string file = options.File ?? throw new ArgumentException("A source or artifact filename is required; use --help for commands.");
            if (options.Command == "fmt") return Complete(Format(file, options), telemetry);
            if (file.EndsWith(".mobj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Complete(await Legacy(args), telemetry);
            bool binary = IsArtifact(file);
            string? source = binary ? null : await ReadSource(file);
            if (source is not null && IsLegacySource(source)) return Complete(await Legacy(args, file == "-" ? source : null), telemetry);
            long compileStart = Stopwatch.GetTimestamp();
            Module module = binary ? Artifact.Read(file) : SourceCompiler.Compile(source!, file, options.Optimization);
            telemetry?.Observe(new("compile", "ok", Source: file, DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds * 1_000_000)));
            if (options.Command == "check") { Console.WriteLine($"{file}: valid"); return Complete(0, telemetry); }
            if (options.Command is "compile" or "emit-ir")
            {
                if (options.Output is null) PrintPlan(module);
                else Write(module, options.Output);
                return Complete(0, telemetry);
            }
            if (options.Command == "bind-ir")
            {
                string? sourceFile = options.SourceFile is null ? null : Path.GetFullPath(options.SourceFile);
                Module bound = Binding.Bind(module, new(Environment.CurrentDirectory, HostContext.HomeDirectory, sourceFile));
                Write(bound, options.Output ?? Path.ChangeExtension(file, ".mexe"));
                return Complete(0, telemetry);
            }
            if (options.Command is "plan" or "plan-ir") { PrintPlan(module); return Complete(0, telemetry); }
            using CancellationTokenSource cancellation = new();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                Interpreter interpreter = new(module, RuntimeOptions(options, options.SourceFile ?? (binary || file == "-" ? null : Path.GetFullPath(file)), telemetry));
                await interpreter.ExecuteAsync(cancellation.Token);
                return Complete(interpreter.FailedTransfers == 0 ? 0 : 1, telemetry);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException error) { telemetry?.Finish("cancelled", error); Console.Error.WriteLine("msync: operation cancelled"); return 130; }
        catch (Exception error) when (error is CompileException or SyntaxException or MsyncException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            telemetry?.Finish("error", error);
            Console.Error.WriteLine($"msync: {error.Message}");
            for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException) Console.Error.WriteLine($"  caused by {cause.Message}");
            return 1;
        }
        finally { telemetry?.Dispose(); }
    }

    private static int Complete(int code, TelemetrySession? telemetry)
    {
        telemetry?.Finish(code == 0 ? "ok" : "error");
        return code;
    }

    private static ExecutionOptions RuntimeOptions(Options options, string? sourceFile, TelemetrySession? telemetry) =>
        new(SourceFile: sourceFile, DryRun: options.DryRun, Jobs: options.Jobs, BufferSize: options.BufferSize,
            MemoryLimit: options.MemoryLimit, SpoolLimit: options.SpoolLimit, Observe: telemetry is null ? null : telemetry.Observe);

    private static EditorDiagnostic? CheckForEditor(string source, string? name)
    {
        try { _ = SourceCompiler.Compile(source, name); return null; }
        catch (CompileException error) { return new(error.Message, error.Line, error.Column, error.Span.Start, error.Span.Length); }
        catch (SyntaxException error) { return new(error.Message, error.Line, error.Column, error.Span.Start, error.Span.Length); }
    }

    private static bool IsArtifact(string file) => file.EndsWith(".mso", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase);
    private static bool IsLegacySource(string source) => source.Split('\n').Take(5).Any(line => line.Trim().StartsWith("# msync: 0.", StringComparison.Ordinal) && !line.Trim().StartsWith("# msync: 0.8", StringComparison.Ordinal));
    private static Task<string> ReadSource(string file) => file == "-" ? Console.In.ReadToEndAsync() : File.ReadAllTextAsync(file);
    private static void Write(Module module, string output)
    {
        if (output != "-") { Artifact.Write(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        Artifact.Write(module, stream);
    }

    private static void PrintPlan(Module module)
    {
        Console.WriteLine($"{module.Stage} bytecode, optimization O{module.OptimizationLevel}, {module.Functions.Count} functions, {module.Constants.Count} constants");
        for (int f = 0; f < module.Functions.Count; f++)
        {
            FunctionCode function = module.Functions[f];
            Console.WriteLine($"fn #{f} {function.Name}({function.ParameterCount} arguments) -> {function.ReturnType}");
            for (int i = 0; i < function.Instructions.Count; i++)
            {
                Instruction instruction = function.Instructions[i];
                Console.WriteLine($"  {i,4}: {instruction.Op,-16} {instruction.A} {instruction.B} {instruction.Text}");
            }
        }
        foreach (BasicBlock block in module.Blocks ?? []) Console.WriteLine($"block fn#{block.Function} [{block.Start},{block.End}) effects={block.HasEffects} successors=[{string.Join(',', block.Successors)}]");
    }

    private static int Format(string file, Options options)
    {
        string original = ReadSource(file).GetAwaiter().GetResult();
        string formatted = Formatter.Format(original);
        if (options.FormattingCheck)
        {
            if (formatted == original) return 0;
            Console.Error.WriteLine($"{file}: formatting differs"); return 1;
        }
        if (options.Stdout || file == "-") Console.Write(formatted);
        else File.WriteAllText(file, formatted);
        return 0;
    }

    private static Task<int> Repl(Options options, TelemetrySession? telemetry, Configuration configuration) =>
        new ReplSession(RuntimeOptions(options, null, telemetry), options.Optimization, configuration.Color).RunAsync();

    private static int Doctor()
    {
        foreach (string program in new[] { "ssh", "wsl" }) Console.WriteLine($"{program}: {FindExecutable(program) ?? "not found on PATH"}");
        return 0;
    }

    private static async Task<int> Legacy(string[] args, string? source = null)
    {
        string? executable = Environment.GetEnvironmentVariable("MSYNC_LEGACY");
        executable ??= FindExecutable("msync-legacy");
        if (executable is null)
            throw new ArgumentException("This command/source uses the preserved Rust baseline. Build it with `cargo build --release --manifest-path legacy/Cargo.toml`, then set MSYNC_LEGACY to the resulting msync executable, or run it directly with these arguments.");
        ProcessStartInfo start = new(executable) { UseShellExecute = false, RedirectStandardInput = source is not null };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException($"Cannot launch legacy executable '{executable}'");
        if (source is not null)
        {
            await process.StandardInput.WriteAsync(source);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string? FindExecutable(string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            string candidate = Path.Combine(directory, name + suffix);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record Options(string Command, string? File, string? Output, bool DryRun, int Optimization,
        string? SourceFile, int Jobs, bool FormattingCheck, bool Stdout, string? Syntax,
        string? ConfigPath, bool NoConfig, IReadOnlyDictionary<string, object> Overrides,
        string? TracePath, string? TelemetryDatabase, bool NoTelemetry)
    {
        public int BufferSize { get; init; } = 65536;
        public long MemoryLimit { get; init; } = 67108864;
        public long? SpoolLimit { get; init; }

        public static Options Parse(string[] args)
        {
            string command = args.Length == 0 ? "repl" : "run";
            string? file = null, output = null, source = null, syntax = null, configPath = null, tracePath = null, telemetryDatabase = null;
            Dictionary<string, object> overrides = [];
            int optimization = 1, jobs = 4;
            bool dryRun = false, formattingCheck = false, stdout = false, noConfig = false, noTelemetry = false;
            int index = 0;
            bool commandSeen = false;
            if (args.Length != 0 && Commands.Contains(args[0], StringComparer.Ordinal)) { command = args[0]; index++; commandSeen = true; }
            for (; index < args.Length; index++)
            {
                string arg = args[index];
                string Next() => ++index < args.Length ? args[index] : throw new ArgumentException($"{arg} requires a value.");
                switch (arg)
                {
                    case "-o": case "--output": output = Next(); break;
                    case "-n": case "--dry-run": dryRun = true; break;
                    case "--source-file": source = Next(); break;
                    case "--syntax": syntax = Next(); overrides["language.syntax"] = syntax; break;
                    case "--color": overrides["ui.color"] = Next(); break;
                    case "--jobs": jobs = int.Parse(Next(), CultureInfo.InvariantCulture); if (jobs < 1) throw new ArgumentException("--jobs must be positive."); overrides["execution.jobs"] = jobs; break;
                    case "--buffer-size": overrides["execution.buffer_size"] = Next(); break;
                    case "--memory-limit": overrides["execution.memory_limit"] = Next(); break;
                    case "--spool-limit": overrides["execution.spool_limit"] = Next(); break;
                    case "--check": formattingCheck = true; break;
                    case "--stdout": stdout = true; break;
                    case "-O0": optimization = 0; break;
                    case "-O1": optimization = 1; break;
                    case "-O2": optimization = 2; break;
                    case "--config": configPath = Next(); break;
                    case "--no-config": noConfig = true; break;
                    case "--trace": tracePath = Next(); break;
                    case "--telemetry-db": telemetryDatabase = Next(); break;
                    case "--no-telemetry": noTelemetry = true; break;
                    default:
                        if (arg.StartsWith('-') && arg != "-") throw new ArgumentException($"Unknown option '{arg}'.");
                        if (!commandSeen && file is null && Commands.Contains(arg, StringComparer.Ordinal)) { command = arg; commandSeen = true; break; }
                        if (file is not null) throw new ArgumentException($"Unexpected argument '{arg}'.");
                        file = arg; break;
                }
            }
            return new(command, file, output, dryRun, optimization, source, jobs, formattingCheck, stdout, syntax, configPath, noConfig, overrides, tracePath, telemetryDatabase, noTelemetry);
        }
    }
}
