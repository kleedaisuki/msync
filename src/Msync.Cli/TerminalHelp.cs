using Msync.Compiler;

namespace Msync.Cli;

internal static class TerminalHelp
{
    public static void Show(string? topic, string policy)
    {
        bool color = TerminalPresentation.Colors(policy);
        topic = topic switch { "emit-ir" => "compile", "plan-ir" => "plan", _ => topic };
        if (topic == "syntax") { Syntax(color); return; }
        if (topic is not null && Topics.TryGetValue(topic, out var entry)) { Command(topic, entry, color); return; }
        if (topic is not null)
            throw new ArgumentException($"Unknown help topic '{topic}'. Use 'msync help' for commands or 'msync help syntax' for the language guide.");
        Overview(color);
    }

    private static void Heading(string title, bool color) => Console.WriteLine(TerminalPresentation.Paint(title, TerminalPresentation.HeadingColor, color));
    private static void Row(string term, string description, bool color)
    {
        string padding = new(' ', Math.Max(2, 33 - term.Length));
        Console.WriteLine("  " + TerminalPresentation.Paint(term, TerminalPresentation.CommandColor, color) + padding + description);
    }
    private static void Code(string text, bool color) => Console.WriteLine("  " + TerminalPresentation.Syntax(text, color));

    private static void Overview(bool color)
    {
        Heading("msync 0.8.0 — a typed file synchronization language", color);
        Console.WriteLine();
        Heading("USAGE", color);
        Row("msync [OPTIONS] [run] FILE", "Execute source or compiled bytecode", color);
        Row("msync COMMAND [OPTIONS]", "Use '-' for standard input", color);
        Console.WriteLine();
        Heading("COMMANDS", color);
        Row("run FILE", "Execute .msync, .mso, or .mexe", color);
        Row("check FILE", "Check syntax and types without effects", color);
        Row("compile FILE -o FILE.mso", "Compile relocatable binary bytecode", color);
        Row("bind-ir FILE -o FILE.mexe", "Bind bytecode to the current context", color);
        Row("run-ir FILE", "Execute bytecode without source", color);
        Row("plan FILE", "Inspect typed bytecode and basic blocks", color);
        Row("disassemble FILE [--json]", "Read binary IR without executing it", color);
        Row("fmt FILE", "Format in place, targeting 80 columns", color);
        Row("repl", "Edit and evaluate interactively", color);
        Row("config", "Show effective settings and their origins", color);
        Row("doctor", "Locate optional native SSH/WSL clients", color);
        Row("skill [TOPIC ...]", "Browse progressive language/tooling notes", color);
        Row("help [COMMAND|syntax]", "Read command details or the language guide", color);
        Console.WriteLine();
        Heading("OPTIONS", color);
        Row("-o, --output FILE", "Write bytecode; '-' is binary stdout", color);
        Row("-n, --dry-run", "Inspect work without changing files", color);
        Row("-O0, -O1, -O2, -O3", "Select optimization level (default: O2)", color);
        Row("--source-file FILE", "Set source identity when binding", color);
        Row("--jobs N", "Set copy/process concurrency", color);
        Row("--buffer-size N", "Set transfer buffer size, e.g. 64KiB", color);
        Row("--memory-limit N", "Set the aggregate payload-buffer budget", color);
        Row("--spool-limit N", "Limit process output, or use unlimited", color);
        Row("--check", "With fmt: report differences, do not write", color);
        Row("--stdout", "With fmt: print source, do not write", color);
        Row("--syntax 0.8", "Select the current grammar", color);
        Row("--color auto|always|never", "Style terminal text (default: auto)", color);
        Row("--config FILE", "Read a specific TOML configuration", color);
        Row("--no-config", "Disable configuration discovery", color);
        Row("--trace FILE", "Write opt-in JSONL events; '-' is stderr", color);
        Row("--telemetry-db FILE", "Write opt-in SQLite telemetry", color);
        Row("--no-telemetry", "Disable configured telemetry", color);
        Row("--language-server", "Serve LSP over standard input/output", color);
        Row("-h, --help", "Show help; COMMAND --help adds details", color);
        Row("-V, --version", "Show the version", color);
        Console.WriteLine();
        Heading("START HERE", color);
        Row("msync check sync.msync", "Check before performing transfers", color);
        Row("msync --dry-run sync.msync", "Preview selected work", color);
        Row("msync sync.msync", "Execute the script", color);
        Console.WriteLine("\nNO_COLOR disables terminal color, including --color always.");
        Console.WriteLine("Source, bytecode, configuration, and LSP output are never decorated.");
    }

    private static void Command(string name, Topic topic, bool color)
    {
        Heading($"msync {name} — {topic.Summary}", color);
        Console.WriteLine();
        Heading("USAGE", color);
        Code("msync " + topic.Usage, color);
        Console.WriteLine();
        foreach (string paragraph in topic.Notes) Console.WriteLine(paragraph + "\n");
        Heading("EXAMPLES", color);
        foreach (string example in topic.Examples) Code(example, color);
        Console.WriteLine();
        Console.WriteLine("Global options may precede or follow the command. See msync --help.");
    }

    public static void Repl(string policy)
    {
        bool color = TerminalPresentation.Colors(policy);
        Heading("SESSION COMMANDS", color);
        Row("/help, /?", "Show session commands", color);
        Row("/syntax", "Read the language guide", color);
        Row("/quit, /exit", "Leave the session", color);
        Row("/pwd", "Print the working directory", color);
        Row("/cd [PATH]", "Change directory; no argument selects home", color);
        Row("/clear", "Clear the view, retaining bindings", color);
        Console.WriteLine();
        Heading("TYPED TERMINAL TOOLS", color);
        Row("pwd(); cd(./work)", "Inspect or change this session's local directory", color);
        Row("ls(); ls_at(~)", "Stream Path values from the selected directory", color);
        Row("cat(./notes.txt)", "Read UTF-8 text; existing Path values retain their origin", color);
        Row("grep(\"error\", ./log.txt)", "Select matching lines as a composable Range", color);
        Row("head(sort(uniq(lines)), 10)", "Compose values, not shell command strings", color);
        Row("msync skill library shell", "Read the complete typed command reference", color);
        Console.WriteLine("These are ordinary shadowable functions imported only by the REPL.");
        Console.WriteLine("Scripts select the same operations through $std::shell::operation(...).\n");
        Heading("EDITING", color);
        Row("Tab", "Complete names, members and selected paths", color);
        Row("Shift+Enter", "Insert a newline", color);
        Row("Up / Down", "Recall earlier submissions", color);
        Row("Right / End", "Accept an insertable gray hint", color);
        Row("Ctrl+C", "Cancel input or running work", color);
        Row("Ctrl+D / Ctrl+Z", "Leave when the input is empty", color);
        Console.WriteLine("\nEnter submits only typed source, not the gray hint.");
        Console.WriteLine("Bracketed context hints are explanatory and are never inserted.");
        Console.WriteLine("Bindings persist; previous transfers are not replayed.");
        Console.WriteLine("An unfinished expression continues on the next line.");
        Console.WriteLine("End a statement with ';' to discard its value; omit it to inspect the result.");
    }

    public static void Syntax(bool color)
    {
        Heading("msync 0.8.0 — language guide", color);
        Console.WriteLine();
        Heading("VALUES AND BLOCKS", color);
        Code("let source = ~/.codex;", color);
        Code("let selected = if exists source { source } else { ~/backup };", color);
        Console.WriteLine("\nBraces delimit blocks; indentation is not syntax. A final expression returns");
        Console.WriteLine("its value. A semicolon discards that value. Assignment changes values, not");
        Console.WriteLine("inferred types. Later declarations may shadow names. Unbound names are Paths.");
        Code("selected = ~/another-backup;", color);
        Code("let .codex = ~/.codex;", color);
        Console.WriteLine(".codex is a legal binding name; a matching binding wins over a bare Path.");
        Console.WriteLine();
        Heading("TRANSFERS AND CONNECTIONS", color);
        Code("source -> ~/backup;", color);
        Code("let remote = ssh://forge;", color);
        Code("source -> ~/.codex @ remote;", color);
        Console.WriteLine("\n'->' returns Bool. A String source is literal data; a Path source is a file");
        Console.WriteLine("or directory. $local, $wsl, and SSH URLs are physical Connections.");
        Console.WriteLine();
        Heading("FUNCTIONS AND SCOPES", color);
        Code("fn copy(source: Path | String, destination) -> Bool {", color);
        Code("    source -> destination", color);
        Code("};", color);
        Code("let identity = fn(file: Path) -> Path { file };", color);
        Code("fn update() { global source; source = ~/another-source; };", color);
        Code("scope Settings { home: ~/.codex, copy: fn(a, b) { a -> b } };", color);
        Code("Settings::copy(Settings::home, ~/backup);", color);
        Console.WriteLine("\nUnannotated parameters are generic and inferred at each call. Functions and");
        Console.WriteLine("Scopes are values and may be anonymous. Scope::member selects a lexical");
        Console.WriteLine("member; path @ connection selects a physical endpoint. These are distinct types.");
        Console.WriteLine("Named Scopes are shared singletons; anonymous scope expressions create fresh");
        Console.WriteLine("instances. Both permit mutable values with fixed field types.");
        Console.WriteLine("Inside fn, 'global source;' explicitly imports a prior ancestor location.");
        Console.WriteLine("It shares assignments, not a snapshot; callers and later declarations are");
        Console.WriteLine("not searched. Existing implicit captures remain valid.");
        Console.WriteLine("Write '-> Type' after the parameter list to constrain the return value.");
        Console.WriteLine("It checks block tails and explicit returns; '-> Path?' also admits Null.");
        Console.WriteLine();
        Heading("NUMBERS, CONVERSION AND CONCEPTS", color);
        Code("let total = (1_024.5 + .5) / 2;", color);
        Code("let parsed = \"42\" as Number;", color);
        Code("concept Named { name: String, };", color);
        Code("fn name_of(value: Named) -> String { value::name };", color);
        Console.WriteLine("\nNumber is binary64. Decimal, exponent and integer base forms are supported.");
        Console.WriteLine("Only Path/String convert implicitly; use 'as Type' for explicit conversion.");
        Console.WriteLine("Concepts describe structural members and function signatures, not new runtime");
        Console.WriteLine("types. Read 'msync skill language concepts' for composition and algebra.");
        Console.WriteLine();
        Heading("NULL AND MATCHING", color);
        Code("let source? = if exists ~/.codex { ~/.codex } else { null };", color);
        Code("if source is not null { source -> ~/backup };", color);
        Code("let label = match source { null => { \"missing\" }, _ => { \"found\" } };", color);
        Console.WriteLine("\nAdmit Null explicitly with '?' on a binding or type. Test with 'is null' or");
        Console.WriteLine("'is not null' before using the value. Postfix 'source?' means IS NULL, not");
        Console.WriteLine("is-present. Null has no implicit truth value. Every match must be exhaustive.");
        Console.WriteLine();
        Heading("RANGES, TYPES, AND PROCESSES", color);
        Code("for file in ~/.codex/<.+> { file -> ~/backup };", color);
        Code("let kind = decltype(~/.codex);", color);
        Code("let process = ./tool(\"hello\");", color);
        Console.WriteLine("\nRegex patterns also work in match; Paths match as endpoint-native strings.");
        Console.WriteLine("decltype infers without executing its expression. Types may be matched.");
        Console.WriteLine("Calling a Path starts a native Process. Native declarations describe host");
        Console.WriteLine("capabilities: native fn name(value: String) -> Bool;");
        Console.WriteLine();
        Heading("ORDERING AND PROCESS LIFETIMES", color);
        Code("let process = release ./tool(\"hello\");", color);
        Code("acquire process;", color);
        Console.WriteLine("\nrelease orders earlier work before its operand without immediately joining");
        Console.WriteLine("a launched Process. acquire process waits for that Process, not unrelated");
        Console.WriteLine("work. acquire expression applies an after fence; acqrel applies both.");
        Console.WriteLine("Bare acquire joins this invocation's pending work. Bare release is only a");
        Console.WriteLine("before fence; it does not change how later unmarked operations execute.");
        Console.WriteLine();
        Heading("TYPES", color);
        foreach (string[] names in LanguageTypes.Names.Chunk(6))
            Code(string.Join("  ", names.Select(name => name == "Range" ? "Range<T>" : name)), color);
        Console.WriteLine("\nFor an explicit constraint: let file: Path = ~;");
        Console.WriteLine("Path and String convert implicitly; an annotation cannot turn a Path into");
        Console.WriteLine("a lexical Scope or physical Connection. String quotes escape by doubling:");
        Code("let message = \"say \"\"hello\"\"\";", color);
        Console.WriteLine("\nComments start with # or // at a whitespace boundary. Newlines inside");
        Console.WriteLine("Strings remain data. Use 'msync fmt FILE' to format without changing literals.");
    }

    private sealed record Topic(string Summary, string Usage, string[] Notes, string[] Examples);
    private static readonly Dictionary<string, Topic> Topics = new(StringComparer.Ordinal)
    {
        ["skill"] = new("browse the built-in reference", "skill [TOPIC ...]", ["Start with 'msync skill' for a compact topic index, then follow a topic path\nfor focused language and tooling details. Output is Markdown when redirected.", "Reading the reference does not execute code or initialize telemetry."], ["msync skill", "msync skill language functions"]),
        ["run"] = new("execute source or bytecode", "[run] FILE [-n] [--jobs N]", ["Accepts .msync source, relocatable .mso, or bound .mexe bytecode.\nUse '-' to read source from standard input. Ctrl+C cancels active work.", "A failed transfer leaves completed files in place; fix the cause and retry.\n--dry-run previews selected work without modifying transfer targets."], ["msync --dry-run sync.msync", "msync sync.msync"]),
        ["check"] = new("check syntax and inferred types", "check FILE [-O0|-O1|-O2|-O3]", ["Checks the program without running user effects or connecting to endpoints.\nGeneric functions are checked for their selected argument specializations."], ["msync check sync.msync", "msync --color always check sync.msync"]),
        ["compile"] = new("compile relocatable bytecode", "compile FILE [-o FILE.mso] [-O0|-O1|-O2|-O3]", ["Compilation infers types and specializes generic functions. The resulting\n.mso is machine independent; paths retain context until binding or execution.", "Without -o, print a human-readable plan. '-o -' writes only binary bytecode\nto standard output, with diagnostics on standard error."], ["msync compile sync.msync -o sync.mso", "msync run-ir sync.mso"]),
        ["bind-ir"] = new("bind bytecode to this context", "bind-ir FILE.mso [-o FILE.mexe] [--source-file FILE]", ["Captures the current working directory, home directory, and optional source\nidentity. The bound .mexe remains executable without the original source.", "Binding does not cache credentials or freeze filesystem observations.\nWithout -o, writes beside the input using the .mexe extension."], ["msync bind-ir sync.mso -o sync.mexe", "msync run-ir sync.mexe"]),
        ["run-ir"] = new("execute compiled bytecode", "run-ir FILE.mso|FILE.mexe [-n]", ["Executes typed binary bytecode, not embedded source. A relocatable .mso uses\nthe current context; a .mexe retains its bound context."], ["msync run-ir sync.mso", "msync --dry-run run-ir sync.mexe"]),
        ["plan"] = new("inspect typed bytecode", "plan FILE", ["Prints typed functions, instructions, and basic blocks without executing\nuser effects. Accepts source and either binary bytecode stage."], ["msync plan sync.msync", "msync plan sync.mexe"]),
        ["disassemble"] = new("read compiled artifacts", "disassemble FILE.mso|FILE.mexe [--json]", ["Reconstructs a read-only typed IR view with constants, function signatures,\nlocal slots, block targets, bound context and source-span provenance.\nIt does not bind, optimize, execute code, authenticate or load configuration.", "Optimized-away source cannot be recovered. This view describes the actual\nbytecode, not invented source. Use '-' for binary stdin. --json emits a clean\nversioned document model used by the VS Code artifact viewer."], ["msync disassemble sync.mso", "msync disassemble sync.mexe --json"]),
        ["fmt"] = new("format source", "fmt FILE [--check|--stdout]", ["Formats in place by default. --stdout prints the result instead; --check\nreturns 1 if formatting differs and never writes. '-' reads standard input.", "The target is 80 columns. Long calls and expressions break at syntax\nboundaries; literals are not split merely to meet a width target."], ["msync fmt sync.msync", "msync fmt sync.msync --check", "msync fmt sync.msync --stdout"]),
        ["repl"] = new("evaluate interactively", "repl", ["Bindings and functions persist across submissions. Earlier effects are not\nreplayed. Tab completes names and paths; /help lists session commands.", "Shift+Enter inserts a newline. An unfinished expression continues naturally.\nCtrl+C cancels input or running work without discarding earlier bindings."], ["msync repl"]),
        ["config"] = new("inspect effective configuration", "config [--config FILE|--no-config]", ["Prints TOML settings and their origins without creating runtime state.\nCommand-line options override discovered configuration values."], ["msync config", "msync --no-config config", "msync --config settings.toml config"]),
        ["doctor"] = new("inspect optional transports", "doctor", ["Locates native ssh and wsl executables on PATH. A local-only script needs\nneither transport. Native clients retain their aliases, agents, and prompts."], ["msync doctor"])
    };
}
