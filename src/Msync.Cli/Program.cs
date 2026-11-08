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
    private static readonly string[] Commands = ["run", "check", "compile", "emit-ir", "bind-ir", "run-ir", "plan", "plan-ir", "fmt", "repl", "doctor", "config", "help", "skill"];

    public static async Task<int> Main(string[] args)
    {
        TelemetrySession? telemetry = null;
        SourceSnapshot? snapshot = null;
        string color = TerminalPresentation.RequestedColor(args);
        try
        {
            if (args.Length != 0 && args[0] == "legacy") return await Legacy(args[1..]);
            if (args.Contains("--language-server", StringComparer.Ordinal)) return await new LanguageServer(CheckForEditor, source => Formatter.Format(source)).RunAsync();
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
            {
                Options help = Options.Parse(args.Where(arg => arg is not ("--help" or "-h")).ToArray());
                string? topic = help.Command == "help" ? help.File : help.ExplicitCommand || help.File is not null ? help.Command : null;
                TerminalHelp.Show(topic, color);
                return 0;
            }
            if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-V", StringComparer.Ordinal)) { Console.WriteLine("msync 0.8.0"); return 0; }
            Options options = Options.Parse(args);
            if (options.Command == "help") { TerminalHelp.Show(options.File, color); return 0; }
            if (options.Command == "skill") { TerminalPresentation.Reference(SkillReference.Render(options.SkillTopics), color); return 0; }
            Configuration configuration = Configuration.Load(options.ConfigPath, options.NoConfig, options.Overrides);
            if (options.Command == "config") { configuration.Dump(Console.Out); return 0; }
            configuration.Validate();
            color = configuration.Color;
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
            if (source is not null) snapshot = new(file, source);
            if (source is not null && IsLegacySource(source)) return Complete(await Legacy(args, file == "-" ? source : null), telemetry);
            long compileStart = Stopwatch.GetTimestamp();
            Module module = binary ? Artifact.Read(file) : SourceCompiler.Compile(source!, file, options.Optimization);
            telemetry?.Observe(new("compile", "ok", Source: file, DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds * 1_000_000)));
            if (options.Command == "check") { Console.WriteLine($"{file}: valid"); return Complete(0, telemetry); }
            if (options.Command is "compile" or "emit-ir")
            {
                if (options.Output is null) PrintPlan(module, color);
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
            if (options.Command is "plan" or "plan-ir") { PrintPlan(module, color); return Complete(0, telemetry); }
            using CancellationTokenSource cancellation = new();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                Interpreter interpreter = new(module, RuntimeOptions(options, options.SourceFile ?? (binary || file == "-" ? null : Path.GetFullPath(file)), telemetry, color, snapshot));
                await interpreter.ExecuteAsync(cancellation.Token);
                return Complete(interpreter.FailedTransfers == 0 ? 0 : 1, telemetry);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException error) { telemetry?.Finish("cancelled", error); TerminalPresentation.Cancelled(color); return 130; }
        catch (Exception error) when (error is CompileException or SyntaxException or MsyncException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            telemetry?.Finish("error", error);
            TerminalPresentation.Error(error, color, snapshot);
            return 1;
        }
        finally { telemetry?.Dispose(); }
    }

    private static int Complete(int code, TelemetrySession? telemetry)
    {
        telemetry?.Finish(code == 0 ? "ok" : "error");
        return code;
    }

    private static ExecutionOptions RuntimeOptions(Options options, string? sourceFile, TelemetrySession? telemetry,
        string color, SourceSnapshot? snapshot = null) =>
        new(SourceFile: sourceFile, DryRun: options.DryRun, Jobs: options.Jobs, BufferSize: options.BufferSize,
            MemoryLimit: options.MemoryLimit, SpoolLimit: options.SpoolLimit, Observe: telemetry is null ? null : telemetry.Observe)
        {
            ReportError = error => TerminalPresentation.Error(error, color, snapshot,
                "Check the source, destination and permissions, then retry. Completed transfers are retained.")
        };

    private static EditorAnalysis CheckForEditor(string source, string? name) =>
        EditorCompiler.Analyze(source, name ?? "<source>");

    private static bool IsArtifact(string file) => file.EndsWith(".mso", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase);
    private static bool IsLegacySource(string source) => source.Split('\n').Take(5).Any(line => line.Trim().StartsWith("# msync: 0.", StringComparison.Ordinal) && !line.Trim().StartsWith("# msync: 0.8", StringComparison.Ordinal));
    private static ValueTask<string> ReadSource(string file) => file == "-"
        ? new(Console.In.ReadToEndAsync()) : ValueTask.FromResult(File.ReadAllText(file));
    private static void Write(Module module, string output)
    {
        if (output != "-") { Artifact.Write(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        Artifact.Write(module, stream);
    }

    private static void PrintPlan(Module module, string policy)
    {
        bool color = TerminalPresentation.Colors(policy);
        Console.WriteLine(TerminalPresentation.Paint($"{module.Stage} bytecode, optimization O{module.OptimizationLevel}, {module.Functions.Count} functions, {module.Constants.Count} constants", TerminalPresentation.HeadingColor, color));
        for (int f = 0; f < module.Functions.Count; f++)
        {
            FunctionCode function = module.Functions[f];
            Console.WriteLine(TerminalPresentation.Syntax($"fn #{f} {function.Name}({function.ParameterCount} arguments) -> {function.ReturnType}", color));
            for (int i = 0; i < function.Instructions.Count; i++)
            {
                Instruction instruction = function.Instructions[i];
                string operation = TerminalPresentation.Paint(instruction.Op.ToString().PadRight(16), "36", color);
                Console.WriteLine($"  {i,4}: {operation} {instruction.A} {instruction.B} {instruction.Text}");
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
        new ReplSession(RuntimeOptions(options, null, telemetry, configuration.Color), options.Optimization, configuration.Color).RunAsync();

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
        public bool ExplicitCommand { get; init; }
        public IReadOnlyList<string> SkillTopics { get; init; } = [];
        public int BufferSize { get; init; } = 65536;
        public long MemoryLimit { get; init; } = 67108864;
        public long? SpoolLimit { get; init; }

        public static Options Parse(string[] args)
        {
            string command = args.Length == 0 ? "repl" : "run";
            string? file = null, output = null, source = null, syntax = null, configPath = null, tracePath = null, telemetryDatabase = null;
            Dictionary<string, object> overrides = [];
            List<string> skillTopics = [];
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
                        if (command == "skill") { skillTopics.Add(arg); break; }
                        if (file is not null) throw new ArgumentException($"Unexpected argument '{arg}'.");
                        file = arg; break;
                }
            }
            return new(command, file, output, dryRun, optimization, source, jobs, formattingCheck, stdout, syntax, configPath, noConfig, overrides, tracePath, telemetryDatabase, noTelemetry) { SkillTopics = skillTopics, ExplicitCommand = commandSeen };
        }
    }
}
