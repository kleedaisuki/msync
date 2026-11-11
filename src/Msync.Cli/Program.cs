using System.Diagnostics;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;
using static Msync.Cli.ArtifactCommands;
using static Msync.Cli.SourceCommands;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (await SshPromptBroker.TryRunHelperAsync(args) is { } helperExit) return helperExit;
        TelemetrySession? telemetry = null;
        ConsoleEncodingScope? consoleEncoding = null;
        SourceSnapshot? snapshot = null;
        string color = TerminalPresentation.RequestedColor(args);
        try
        {
            if (args.Contains("--language-server", StringComparer.Ordinal)) return await new LanguageServer(source => Formatter.Format(source)).RunAsync();
            consoleEncoding = ConsoleEncodingScope.Open();
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
            {
                CommandOptions help = CommandOptions.Parse(args.Where(arg => arg is not ("--help" or "-h")).ToArray());
                string? topic = help.Command == "help" ? help.File : help.ExplicitCommand || help.File is not null ? help.Command : null;
                TerminalHelp.Show(topic, color);
                return 0;
            }
            if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-V", StringComparer.Ordinal)) { Console.WriteLine($"msync {ProductVersion.Current}"); return 0; }
            CommandOptions options = CommandOptions.Parse(args);
            if (options.Command == "help") { TerminalHelp.Show(options.File, color); return 0; }
            if (options.Command == "skill") { TerminalPresentation.Reference(SkillReference.Render(options.SkillTopics), color); return 0; }
            if (options.Command == "disassemble")
            {
                if (options.Output is not null) throw new ArgumentException("Disassembly writes text to stdout; redirect it to a separate text file instead of using -o.");
                string artifact = options.File ?? throw new ArgumentException("An artifact filename is required; use 'msync disassemble --help'.");
                byte[] bytes = artifact == "-" ? await ReadArtifactInput() : await File.ReadAllBytesAsync(artifact);
                string identity = artifact == "-" ? "<stdin>" : new Uri(Path.GetFullPath(artifact)).AbsoluteUri;
                ArtifactDisassemblyResult document = ArtifactDisassembly.Read(bytes, identity);
                if (options.Json) Console.WriteLine(document.ToJson().ToJsonString());
                else Console.Write(TerminalPresentation.Syntax(document.Text, TerminalPresentation.Colors(color)));
                return 0;
            }
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
            telemetry = TelemetrySession.TryOpen(configuration, options.Command, options.File, options.TracePath, options.TelemetryDatabase, options.NoTelemetry);
            if (options.Command == "repl") return Complete(await Repl(options, telemetry, configuration), telemetry);
            if (options.Command == "doctor") return Complete(Doctor(), telemetry);
            string file = options.File ?? throw new ArgumentException("A source or artifact filename is required; use --help for commands.");
            if (options.Command == "fmt") return Complete(Format(file, options), telemetry);
            bool hirInput = HirCommands.IsArtifact(file);
            bool binary = hirInput || IsArtifact(file);
            string? source = binary ? null : await ReadSource(file);
            if (source is not null) snapshot = new(file, source);
            long compileStart = Stopwatch.GetTimestamp();
            HirModule? semanticInput = hirInput ? HirArtifact.Read(file) : null;
            if (semanticInput is not null)
            {
                HirSourceId origin = semanticInput.Node(semanticInput.Functions[semanticInput.Entry.Value].Body).Source.Source;
                if (origin.Value >= 0 && semanticInput.Sources[origin.Value] is { Text: { } text } document)
                    snapshot = new(document.Name, text);
            }
            if (options.Emission == CompileEmission.Hir)
            {
                if (binary && !hirInput)
                    throw new ArgumentException("Typed HIR cannot be reconstructed from lowered bytecode. Compile the original .msync source with '--emit hir'.");
                HirModule semantic = semanticInput ?? SourceCompiler.Analyze(source!, file);
                if (options.Output is { } output) HirCommands.Write(semantic, output);
                else HirCommands.Print(semantic, color);
                telemetry?.Observe(new("compile", "ok", Source: file, DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds * 1_000_000)));
                return Complete(0, telemetry);
            }
            if (options.Command == "check")
            {
                if (semanticInput is null)
                {
                    if (binary) _ = Artifact.Read(file);
                    else _ = SourceCompiler.Analyze(source!, file);
                }
                telemetry?.Observe(new("compile", "ok", Source: file, DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds * 1_000_000)));
                Console.WriteLine($"{file}: valid");
                return Complete(0, telemetry);
            }
            Module module = semanticInput is not null ? HirCommands.Lower(semanticInput, options.Optimization)
                : binary ? Artifact.Read(file) : SourceCompiler.Compile(source!, file, options.Optimization);
            telemetry?.Observe(new("compile", "ok", Source: file, DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds * 1_000_000)));
            if (options.Command is "compile" or "emit-ir")
            {
                if (options.Output is null) PrintPlan(module, color);
                else Write(module, options.Output);
                return Complete(0, telemetry);
            }
            using CancellationTokenSource cancellation = new();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                if (options.Command == "bind-ir")
                {
                    string? sourceFile = options.SourceFile is null ? null : Path.GetFullPath(options.SourceFile);
                    Module bound = Binding.Bind(module, new(Environment.CurrentDirectory, HostContext.HomeDirectory, sourceFile));
                    Module prepared = ContextualBackend.Prepare(bound,
                        RuntimeOptions(options, sourceFile, telemetry, color, snapshot), cancellation.Token);
                    Write(prepared, options.Output ?? Path.ChangeExtension(file, ".mexe"));
                    return Complete(0, telemetry);
                }
                if (options.Command is "plan" or "plan-ir")
                {
                    if (module.Stage == ArtifactStage.Relocatable)
                    {
                        string? sourceFile = options.SourceFile is { } explicitSource ? Path.GetFullPath(explicitSource)
                            : binary || file == "-" ? null : Path.GetFullPath(file);
                        module = ContextualBackend.Prepare(Binding.Bind(module,
                            new(Environment.CurrentDirectory, HostContext.HomeDirectory, sourceFile)),
                            RuntimeOptions(options, sourceFile, telemetry, color, snapshot), cancellation.Token);
                    }
                    PrintPlan(module, color);
                    return Complete(0, telemetry);
                }
                Interpreter interpreter = new(module, RuntimeOptions(options, options.SourceFile ?? (binary || file == "-" ? null : Path.GetFullPath(file)), telemetry, color, snapshot), cancellation.Token);
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
        finally { using (consoleEncoding) telemetry?.Dispose(); }
    }

    private static int Complete(int code, TelemetrySession? telemetry)
    {
        telemetry?.Finish(code == 0 ? "ok" : "error");
        return code;
    }

    private static ExecutionOptions RuntimeOptions(CommandOptions options, string? sourceFile, TelemetrySession? telemetry,
        string color, SourceSnapshot? snapshot = null) =>
        new(SourceFile: sourceFile, DryRun: options.DryRun, Jobs: options.Jobs, BufferSize: options.BufferSize,
            MemoryLimit: options.MemoryLimit, SpoolLimit: options.SpoolLimit, Observe: telemetry is null ? null : telemetry.Observe)
        {
            ReportError = error => TerminalPresentation.Error(error, color, snapshot,
                "Check the source, destination and permissions, then retry. Completed transfers are retained.")
        };

    private static Task<int> Repl(CommandOptions options, TelemetrySession? telemetry, Configuration configuration) =>
        new ReplSession(RuntimeOptions(options, null, telemetry, configuration.Color), options.Optimization, configuration.Color).RunAsync();

    private static int Doctor()
    {
        foreach (string program in new[] { "ssh", "wsl" }) Console.WriteLine($"{program}: {FindExecutable(program) ?? "not found on PATH"}");
        return 0;
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

}
