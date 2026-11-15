using System.Diagnostics;
using System.Globalization;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using static Msync.Cli.ArtifactCommands;
using static Msync.Cli.CommandPipeline;
using static Msync.Cli.SourceCommands;

namespace Msync.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        long entered = Stopwatch.GetTimestamp();
        if (await SshPromptBroker.TryRunHelperAsync(args) is { } helperExit) return helperExit;
        TelemetrySession? telemetry = null;
        ConsoleEncodingScope? consoleEncoding = null;
        SourceSnapshot? snapshot = null;
        string color = TerminalPresentation.RequestedColor(args);
        try
        {
            if (args.Contains("--language-server", StringComparer.Ordinal)) return await LanguageServerHost.RunAsync(args, entered);
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
                if (NativeArtifact.HasMagic(bytes))
                {
                    using MemoryStream stream = new(bytes, writable: false);
                    NativeProgram native = NativeCommandPipeline.Read(stream);
                    if (options.Json) Console.WriteLine(NativeArtifact.ToJson(native));
                    else NativeCommandPipeline.Print(native, color);
                    return 0;
                }
                ArtifactDisassemblyResult document = ArtifactDisassembly.Read(bytes, identity);
                if (options.Json) Console.WriteLine(document.ToJson().ToJsonString());
                else Console.Write(TerminalPresentation.Syntax(document.Text, TerminalPresentation.Colors(color)));
                return 0;
            }
            long configurationStarted = Stopwatch.GetTimestamp();
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
            long exporterStarted = Stopwatch.GetTimestamp();
            telemetry = TelemetrySession.TryOpen(configuration, options.Command, options.File, options.TracePath, options.TelemetryDatabase, options.NoTelemetry);
            telemetry?.RecordStartup(entered, configurationStarted, exporterStarted);
            telemetry?.SetAttribute("optimization.level", options.Optimization.ToString(CultureInfo.InvariantCulture));
            bool executes = options.Command is "run" or "run-ir" or "repl";
            if (executes) consoleEncoding ??= ConsoleEncodingScope.Open(redirectedText: true);
            using TextReader? input = executes ? (TextReader?)RedirectedTextInput.Open() ?? new TerminalProgramInput() : null;
            if (options.Command == "repl")
            {
                telemetry?.SetAttribute("input.kind", "repl");
                return Complete(await Repl(options, telemetry, configuration, input), telemetry);
            }
            if (options.Command == "doctor") return Complete(Doctor(), telemetry);
            string file = options.File ?? throw new ArgumentException("A source or artifact filename is required; use --help for commands.");
            if (options.Command == "fmt") return Complete(Format(file, options), telemetry);
            bool hirInput = HirCommands.IsArtifact(file);
            bool binary = hirInput || IsArtifact(file);
            telemetry?.SetAttribute("input.kind", binary ? InputKind(file) : "source");
            string? source = binary ? null : await ReadProgramSource(file, input, telemetry);
            if (source is not null) snapshot = new(file, source);
            HirModule? semanticInput = hirInput ? ReadHir(file, telemetry) : null;
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
                HirModule semantic = semanticInput ?? Analyze(source!, file, telemetry);
                if (options.Output is { } output) WriteHir(semantic, output, telemetry);
                else HirCommands.Print(semantic, color);
                return Complete(0, telemetry);
            }
            NativeProgram program = !binary || semanticInput is not null
                ? NativeCommandPipeline.Compile(source, file, semanticInput, options.Optimization, telemetry)
                : NativeCommandPipeline.Read(file, telemetry);
            return Complete(await NativeCommandPipeline.Run(program, options, file, binary,
                telemetry, color, snapshot, input), telemetry);
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

    private static Task<int> Repl(CommandOptions options, TelemetrySession? telemetry, Configuration configuration, TextReader? input) =>
        new ReplSession(RuntimeOptions(options, null, telemetry is null ? null : telemetry.Observe, configuration.Color, input: input)
        with
        { Profile = telemetry?.CreateProfile(), Timeline = telemetry?.Timeline }, options.Optimization, configuration.Color, telemetry).RunAsync();

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
