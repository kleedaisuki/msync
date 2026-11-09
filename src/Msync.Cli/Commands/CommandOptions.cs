using System.Globalization;

namespace Msync.Cli;

internal enum CompileEmission { Bytecode, Hir }

internal sealed record CommandOptions(string Command, string? File, string? Output, bool DryRun, int Optimization,
    string? SourceFile, int Jobs, bool FormattingCheck, bool Stdout, string? Syntax,
    string? ConfigPath, bool NoConfig, IReadOnlyDictionary<string, object> Overrides,
    string? TracePath, string? TelemetryDatabase, bool NoTelemetry)
{
    private static readonly string[] Commands = ["run", "check", "compile", "emit-ir", "bind-ir", "run-ir", "plan", "plan-ir", "disassemble", "fmt", "repl", "doctor", "config", "help", "skill"];

    public bool ExplicitCommand { get; init; }
    public bool Json { get; init; }
    public CompileEmission Emission { get; init; }
    public IReadOnlyList<string> SkillTopics { get; init; } = [];
    public int BufferSize { get; init; } = 65536;
    public long MemoryLimit { get; init; } = 67108864;
    public long? SpoolLimit { get; init; }

    public static CommandOptions Parse(string[] args)
    {
        string command = args.Length == 0 ? "repl" : "run";
        string? file = null, output = null, source = null, syntax = null, configPath = null, tracePath = null, telemetryDatabase = null;
        Dictionary<string, object> overrides = [];
        List<string> skillTopics = [];
        int optimization = 2, jobs = 4;
        bool dryRun = false, formattingCheck = false, stdout = false, noConfig = false, noTelemetry = false, json = false;
        CompileEmission emission = CompileEmission.Bytecode;
        bool explicitEmission = false;
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
                case "--color": overrides["ui.color"] = ColorPolicy(Next()); break;
                case "--jobs": jobs = int.Parse(Next(), CultureInfo.InvariantCulture); if (jobs < 1) throw new ArgumentException("--jobs must be positive."); overrides["execution.jobs"] = jobs; break;
                case "--buffer-size": overrides["execution.buffer_size"] = Next(); break;
                case "--memory-limit": overrides["execution.memory_limit"] = Next(); break;
                case "--spool-limit": overrides["execution.spool_limit"] = Next(); break;
                case "--check": formattingCheck = true; break;
                case "--stdout": stdout = true; break;
                case "--json": json = true; break;
                case "--emit": emission = ParseEmission(Next()); explicitEmission = true; break;
                case "-O0": optimization = 0; break;
                case "-O1": optimization = 1; break;
                case "-O2": optimization = 2; break;
                case "-O3": optimization = 3; break;
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
        if (json && command != "disassemble") throw new ArgumentException("--json is supported by 'msync disassemble'.");
        if (explicitEmission && command is not ("compile" or "emit-ir"))
            throw new ArgumentException("--emit is supported by 'msync compile'. Use '--emit hir' for typed HIR or '--emit bytecode' for relocatable bytecode.");
        if (!explicitEmission && command is ("compile" or "emit-ir")
            && output?.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase) == true)
            emission = CompileEmission.Hir;
        return new(command, file, output, dryRun, optimization, source, jobs, formattingCheck, stdout, syntax, configPath, noConfig, overrides, tracePath, telemetryDatabase, noTelemetry)
        { SkillTopics = skillTopics, ExplicitCommand = commandSeen, Json = json, Emission = emission };
    }

    private static CompileEmission ParseEmission(string value) => value switch
    {
        "hir" => CompileEmission.Hir,
        "bytecode" => CompileEmission.Bytecode,
        _ => throw new ArgumentException($"Unknown emission kind '{value}'. Use 'hir' or 'bytecode'.")
    };

    private static string ColorPolicy(string value) => value is "auto" or "always" or "never" ? value
        : throw new ArgumentException($"Unknown color policy '{value}'. Use auto, always, or never.");
}
