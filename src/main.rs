mod artifact_cli;
mod auth;
mod doctor;
mod editor;
mod presentation;
mod progress;
mod repl;
mod shell;
mod structured;
#[cfg(any(windows, test))]
mod windows_askpass;

use std::env;
use std::fs;
use std::io::{self, IsTerminal, Read, Write};
use std::path::Path;
use std::process::ExitCode;

use msync::compiler::CompileContext;
use msync::language::{self, Version};
use msync::scheduler::ResourceLimits;
use msync::syntax::{self, Report};
use presentation::{CliError, CliResult, IoAction, Issue};

const HELP: &str = "msync — a compiled file synchronization DSL

USAGE
  msync [run] FILE [--dry-run]     Execute a script ('-' reads stdin)
  msync check FILE                Parse and compile without changing files
  msync plan FILE                 Show ordered basic blocks and dependency graphs
  msync compile FILE              Compile to IR; no filesystem or remote probes
  msync compile FILE -o OBJECT    Export a relocatable object for later execution
  msync fmt FILE                  Format the script file in place
  msync repl [--dry-run]          Start a persistent interactive session
  msync emit-ir FILE              Export expanded relocatable IR (including macros)
  msync bind-ir IR                Freeze local IR context without caching proofs
  msync plan-ir IR                Preview reusable IR without executing
  msync run-ir IR [--dry-run]      Execute expanded IR or legacy 0.4 artifacts
  msync doctor                   Inspect optional transports without launching them

OPTIONS
  -n, --dry-run       Validate and preview without writing files
  -o, --output FILE  Artifact output (compile, emit-ir or bind-ir; '-' = stdout)
  --ir-stage STAGE   relocatable (default) or contextual (emit-ir only)
  --source-file FILE Source identity when binding an IR module using $file
  --jobs N           Copy slots and program slots, separately (default: 4 each)
  --max-streams N    Maximum background pipe/socket flows (default: 16)
  --buffer-size N    Buffer bytes per flow (default: 64KiB)
  --memory-limit N   Aggregate payload-buffer budget (default: 64MiB; not RSS)
  --spool-limit N    Combined stdout/stderr bytes per program (default: unlimited)
  --check            Check formatting without modifying the file (fmt only)
  --stdout           Explicit formatting preview, never writes the file (fmt only)
  --color auto|always|never   Color policy (default: auto; honors NO_COLOR)
  --syntax 0.5       Select grammar; must agree with '# msync: VERSION'
  --trace FILE        Write private, local JSONL telemetry ('-' = stderr)
  -h, --help          Show this help
  -V, --version       Show version

EXAMPLES
  msync check sync.msync          Check syntax and bindings before copying
  msync plan sync.msync           Inspect static IR and runtime proof obligations
  msync compile sync.msync -o sync.mobj  Export without binding this machine's files
  msync run sync.msync --dry-run  Preview without transfers or remote probes
  msync fmt sync.msync --check    Check script formatting without rewriting

SOURCE COMPOSITION
  symbol home(space):
      return space::~
  include \"shared.msync\"         Inline source in this lexical environment
  execute \"tasks/<.+\\.msync>\"  Inline sorted scripts with isolated environments

PROGRAMS
  release Program => $path::bash(\"-c\", \"ls\")
  Program::stdout -> stdout.txt
  acquire Program

REPL: /help or /? for commands. No arguments starts the REPL.
Syntax 0.5 is the only supported CLI grammar; migrate earlier scripts explicitly.
An optional '# msync: VERSION' header makes the grammar explicit.
In syntax 0.5 every bare quoted value is literal UTF-8 data, never an implicit file.
Use $pwd::pwd / \"file name\" or $local::\"file name\" for paths containing spaces.
Use $pwd::pwd/file for working-directory paths; $file::this and $file::parent identify the source script.
Pipes and sockets end independently; Ctrl-C cancels active streams.
External programs run asynchronously internally; an unmarked call waits for exit.
Use release to launch without waiting, acquire to join, and acqrel for both effects.
Finite file statements recover as a whole; streams and external effects are not rolled back.
Graceful cancellation waits for started children to exit and may wait forever.
";

const REPL_HELP: &str = "REPL commands
  /help, /?                Show this help
  /quit, /exit              Exit (or Ctrl-D in the interactive editor)
  /clear                   Clear the viewport; keep bindings, history and jobs
  /bash [COMMAND]          Open Bash, or run a command and return
  /pwsh [COMMAND]          Open PowerShell, or run a command and return
  /pwd                     Show working directory
  /cd PATH                 Change working directory
  /bindings                Show current endpoint and path bindings
  /reset                   Clear user bindings
  /dry-run [on|off]         Show or change preview mode
  /load FILE               Execute a file in the current environment
  /check FILE              Check a file without changing the environment
  /plan FILE               Print basic blocks and dependency graphs without executing
  /fmt FILE                Format a script file in place (never executes it)
  /export-ir FILE          Export the last transfer with its captured context
  /export-relocatable FILE Export the last transfer with symbolic context roots
  /load-ir FILE            Execute reusable IR in the current session
  /limits                  Show execution, buffer and output-spool limits
  /syntax [0.5]             Show or select the supported grammar
  /jobs                    Show background jobs and their identifiers
  /wait                    Wait for active streams and external programs
  /stop                    Stop streams; drain already-started external programs
  /kill                    Force-terminate programs; external effects may be partial
Enter DSL statements directly; bindings persist across submissions.
Macro blocks end with a blank line; Ctrl-C discards a pending block.
release starts background work; acquire joins it; acqrel combines both effects.
Unmarked external calls wait; macros acquire their outstanding programs before returning.
Graceful /stop and Ctrl-C may wait forever for a nonterminating external program.
Use /wait or /stop before changing shared cwd, files or syntax through a command.
In supported terminals, Tab completes commands/namespaces and Up/Down browse history.
Ctrl-C clears the edit and stops active work; Ctrl-D exits safely.
Shells share the current directory, not parent bindings or later directory changes.
";

#[derive(Clone, Copy, Default)]
enum Color {
    #[default]
    Auto,
    Always,
    Never,
}

impl Color {
    fn stderr(self) -> bool {
        match self {
            Self::Always => true,
            Self::Never => false,
            Self::Auto => {
                io::stderr().is_terminal()
                    && env::var_os("NO_COLOR").is_none()
                    && !editor::basic_terminal()
            }
        }
    }

    fn stdout(self) -> bool {
        match self {
            Self::Always => true,
            Self::Never => false,
            Self::Auto => {
                io::stdout().is_terminal()
                    && env::var_os("NO_COLOR").is_none()
                    && !editor::basic_terminal()
            }
        }
    }
}

fn requested_color(args: &[String]) -> Color {
    let mut color = Color::Auto;
    let mut args = args.iter();
    while let Some(arg) = args.next() {
        if arg == "--" {
            break;
        }
        if arg != "--color" {
            continue;
        }
        color = match args.next().map(String::as_str) {
            Some("always") => Color::Always,
            Some("never") => Color::Never,
            _ => Color::Auto,
        };
    }
    color
}

#[derive(Clone, Default)]
struct Args {
    command: String,
    file: Option<String>,
    output: Option<String>,
    dry_run: bool,
    write: bool,
    stdout: bool,
    check_format: bool,
    color: Color,
    trace: Option<String>,
    limits: ResourceLimits,
    spool_limit: Option<u64>,
    syntax: Option<Version>,
    ir_stage: Option<String>,
    source_file: Option<String>,
}

fn arguments(mut args: impl Iterator<Item = String>) -> Result<Args, String> {
    let mut options = Args::default();
    let mut positional = false;
    while let Some(arg) = args.next() {
        if positional {
            add_positional(&mut options, arg)?;
            continue;
        }
        match arg.as_str() {
            "--" => positional = true,
            "-h" | "--help" => options.command = "help".into(),
            "-V" | "--version" => options.command = "version".into(),
            "-n" | "--dry-run" => options.dry_run = true,
            "--write" | "-w" => options.write = true,
            "--stdout" => options.stdout = true,
            "--check" => options.check_format = true,
            "-o" | "--output" => {
                let output = args.next().ok_or("--output requires a filename or '-'")?;
                if options.output.replace(output).is_some() {
                    return Err("--output may be specified only once".into());
                }
            }
            "--ir-stage" => {
                options.ir_stage = Some(
                    args.next()
                        .ok_or("--ir-stage requires relocatable or contextual")?,
                )
            }
            "--source-file" => {
                options.source_file = Some(args.next().ok_or("--source-file requires a filename")?)
            }
            "--jobs" => {
                options.limits.jobs =
                    positive(&args.next().ok_or("--jobs requires a positive integer")?)?
            }
            "--max-streams" => {
                options.limits.max_streams = positive(
                    &args
                        .next()
                        .ok_or("--max-streams requires a positive integer")?,
                )?
            }
            "--buffer-size" => {
                options.limits.buffer_bytes = byte_size(
                    &args
                        .next()
                        .ok_or("--buffer-size requires bytes or KiB/MiB")?,
                )?
            }
            "--memory-limit" => {
                options.limits.memory_bytes = byte_size(
                    &args
                        .next()
                        .ok_or("--memory-limit requires bytes or KiB/MiB")?,
                )?
            }
            "--spool-limit" => {
                options.spool_limit = Some(byte_count(
                    &args
                        .next()
                        .ok_or("--spool-limit requires bytes or KiB/MiB")?,
                )?);
            }
            "--color" => {
                options.color = match args.next().as_deref() {
                    Some("auto") => Color::Auto,
                    Some("always") => Color::Always,
                    Some("never") => Color::Never,
                    _ => return Err("--color requires auto, always or never".into()),
                };
            }
            "--trace" => {
                options.trace = Some(args.next().ok_or("--trace requires a filename or '-'")?);
            }
            "--syntax" => {
                options.syntax = Some(
                    args.next()
                        .ok_or("--syntax requires 0.5")?
                        .parse::<Version>()
                        .map_err(str::to_owned)?,
                );
            }
            value if value.starts_with('-') && value != "-" => {
                let suggestion = msync::suggest::closest(
                    value,
                    [
                        "--help",
                        "--version",
                        "--dry-run",
                        "--jobs",
                        "--max-streams",
                        "--buffer-size",
                        "--memory-limit",
                        "--spool-limit",
                        "--check",
                        "--stdout",
                        "--write",
                        "--color",
                        "--trace",
                        "--syntax",
                        "--output",
                    ],
                )
                .map_or_else(
                    || "try --help".to_owned(),
                    |name| format!("did you mean '{name}'?"),
                );
                return Err(format!("unknown option '{value}'; {suggestion}"));
            }
            _ => add_positional(&mut options, arg)?,
        }
    }
    if options.command.is_empty() {
        options.command = "repl".into();
    }
    if options.output.is_some()
        && !matches!(options.command.as_str(), "compile" | "emit-ir" | "bind-ir")
    {
        return Err("--output exports a relocatable object; use 'compile FILE -o OBJECT'".into());
    }
    if options.output.is_some() && options.dry_run {
        return Err(
            "--output already compiles without execution; do not combine it with --dry-run".into(),
        );
    }
    if options.command == "fmt" && options.dry_run {
        return Err("--dry-run previews transfer plans, not formatting; use 'fmt FILE --check' or '--stdout' for a read-only formatting command".into());
    }
    if (options.write || options.check_format || options.stdout) && options.command != "fmt" {
        return Err("--write, --check and --stdout are only valid with fmt".into());
    }
    if options.write && options.check_format {
        return Err("--write and --check are mutually exclusive".into());
    }
    if options.stdout && (options.write || options.check_format) {
        return Err("--stdout cannot be combined with --write or --check".into());
    }
    artifact_cli::validate_options(&options)?;
    options.limits.validate()?;
    Ok(options)
}

fn add_positional(options: &mut Args, arg: String) -> Result<(), String> {
    if options.command.is_empty() {
        if matches!(
            arg.as_str(),
            "run"
                | "check"
                | "plan"
                | "compile"
                | "fmt"
                | "repl"
                | "doctor"
                | "emit-ir"
                | "bind-ir"
                | "plan-ir"
                | "run-ir"
        ) {
            options.command = arg;
            return Ok(());
        }
        options.command = "run".into();
    }
    if options.file.replace(arg).is_some() {
        return Err("expected one input file; try --help".into());
    }
    Ok(())
}

fn main() -> ExitCode {
    #[cfg(windows)]
    if let Some(status) = windows_askpass::handle() {
        return status;
    }
    let args: Vec<_> = env::args().skip(1).collect();
    let color = requested_color(&args);
    let result = arguments(args.into_iter())
        .map_err(CliError::argument)
        .and_then(run);
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            if !message.is_empty() {
                print_error(&message, color);
            }
            ExitCode::FAILURE
        }
    }
}

fn run(options: Args) -> CliResult {
    if let Some(path) = &options.trace {
        msync::telemetry::init(path).map_err(|cause| {
            Issue::new("cannot initialize telemetry")
            .field("path", path)
            .field("cause", cause)
            .help("Choose a writable trace file or use --trace - to write JSONL to standard error.")
        })?;
    }
    let mut trace = msync::telemetry::span("session");
    let result = dispatch(&options);
    if result.is_err() {
        trace.error();
    }
    drop(trace);
    let flushed = msync::telemetry::flush().map_err(|cause| CliError::from(Issue::new("cannot finish telemetry export")
        .field("path", options.trace.as_deref().unwrap_or("active trace sink"))
        .field("cause", cause)
        .help("Check the trace destination and available disk space; completed file transfers are not rolled back.")));
    match (result, flushed) {
        (Err(error), Err(export_error)) => Err(CliError::Presented(format!(
            "{}\n\n{}",
            error.render(options.color),
            export_error.render(options.color)
        ))),
        (Err(error), _) => Err(error),
        (Ok(()), outcome) => outcome,
    }
}

fn dispatch(options: &Args) -> CliResult {
    match options.command.as_str() {
        "help" => print_help(HELP, options.color),
        "version" => print_text(concat!("msync ", env!("CARGO_PKG_VERSION"), "\n")),
        "emit-ir" | "bind-ir" | "plan-ir" | "run-ir" => artifact_cli::run(options),
        "doctor" if options.file.is_none() => doctor::run(options.color),
        "doctor" => Err(CliError::argument(
            "doctor does not take an input file; run msync doctor".into(),
        )),
        "repl" if options.file.is_none() => repl::run(options),
        "repl" => Err("repl does not take a file; use /load inside the REPL".into()),
        _ => run_file(options),
    }
}

fn print_text(text: &str) -> CliResult {
    anstream::AutoStream::new(io::stdout().lock(), anstream::ColorChoice::Always)
        .write_all(text.as_bytes())
        .map_err(|error| format!("cannot write output: {error}").into())
}

fn print_error(message: &CliError, color: Color) {
    let _ = writeln!(
        anstream::AutoStream::new(io::stderr().lock(), anstream::ColorChoice::Always),
        "{}",
        message.render(color)
    );
}

fn print_help(help: &str, color: Color) -> CliResult {
    if !color.stdout() {
        return print_text(help);
    }
    let mut output = String::with_capacity(help.len() + 256);
    for (index, line) in help.lines().enumerate() {
        if index == 0 {
            output.push_str(&presentation::paint(line, "1", true));
            output.push('\n');
        } else if line.starts_with("  ") {
            let content = line.trim_start();
            let end = content.find("  ").unwrap_or(content.len());
            output.push_str(&format!(
                "  \x1b[1;36m{}\x1b[0m{}\n",
                presentation::escape(&content[..end]),
                presentation::escape(&content[end..])
            ));
        } else if !line.is_empty() && line.chars().all(|c| c.is_uppercase() || c.is_whitespace()) {
            output.push_str(&format!("\x1b[1m{line}\x1b[0m\n"));
        } else if !line.is_empty() {
            output.push_str(&msync::highlight::highlight(
                &presentation::escape(line),
                true,
            ));
            output.push('\n');
        } else {
            output.push('\n');
        }
    }
    print_text(&output)
}

fn print_status(text: &str, color: Color) -> CliResult {
    print_text(&format!(
        "{}\n",
        presentation::human(text.trim_end(), color.stdout())
    ))
}

fn print_limits(limits: &ResourceLimits, spool_limit: Option<u64>, color: Color) -> CliResult {
    if io::stdout().is_terminal() {
        return print_text(&presentation::limits(limits, spool_limit, color.stdout()));
    }
    print_status(
        &format!(
            "limits: jobs={}, max-streams={}, buffer-size={} bytes, memory-limit={} bytes, spool-limit={} per program (stdout+stderr)",
            limits.jobs,
            limits.max_streams,
            limits.buffer_bytes,
            limits.memory_bytes,
            spool_limit.map_or_else(|| "unlimited".into(), |bytes| format!("{bytes} bytes"))
        ),
        color,
    )
}

fn positive(value: &str) -> Result<usize, String> {
    value
        .parse::<usize>()
        .ok()
        .filter(|number| *number > 0)
        .ok_or_else(|| format!("'{value}' is not a positive integer; try --help"))
}

fn byte_size(value: &str) -> Result<usize, String> {
    let count = byte_count(value)?;
    if count == 0 {
        return Err(format!(
            "'{value}' is not a positive byte budget; try --help"
        ));
    }
    usize::try_from(count).map_err(|_| "byte budget is too large".into())
}

fn byte_count(value: &str) -> Result<u64, String> {
    let end = value
        .find(|ch: char| !ch.is_ascii_digit())
        .unwrap_or(value.len());
    let number = value[..end]
        .parse::<u64>()
        .map_err(|_| format!("'{value}' is not a nonnegative byte budget; try --help"))?;
    let scale = match value[end..].to_ascii_lowercase().as_str() {
        "" | "b" => 1,
        "k" | "kb" | "kib" => 1024,
        "m" | "mb" | "mib" => 1024 * 1024,
        "g" | "gb" | "gib" => 1024 * 1024 * 1024,
        _ => {
            return Err(format!(
                "invalid byte unit in '{value}'; use bytes, KiB, MiB or GiB"
            ));
        }
    };
    number
        .checked_mul(scale)
        .ok_or_else(|| "byte budget is too large".into())
}

fn source_text(file: &str, bytes: Vec<u8>) -> CliResult<String> {
    String::from_utf8(bytes).map_err(|cause| {
        let action = if file == "-" {
            IoAction::ReadInput
        } else {
            IoAction::ReadScript
        };
        CliError::io(
            action,
            (file != "-").then(|| Path::new(file)),
            io::Error::new(io::ErrorKind::InvalidData, cause),
        )
    })
}

fn read_input(file: &str) -> CliResult<Vec<u8>> {
    let mut trace = msync::telemetry::span("read");
    let result = read_input_inner(file);
    trace.finish_result(&result);
    if let Ok(source) = &result {
        trace.metric("bytes", source.len() as u64);
    }
    result
}

fn read_input_inner(file: &str) -> CliResult<Vec<u8>> {
    if file != "-" {
        let mut reader = fs::File::open(file)
            .map_err(|cause| CliError::io(IoAction::ReadScript, Some(Path::new(file)), cause))?;
        return read_content(&mut reader, file);
    }
    read_content(&mut io::stdin().lock(), file)
}

fn read_content(reader: &mut impl Read, file: &str) -> CliResult<Vec<u8>> {
    let action = if file == "-" {
        IoAction::ReadInput
    } else {
        IoAction::ReadScript
    };
    let failure = |cause| CliError::io(action, (file != "-").then(|| Path::new(file)), cause);
    let mut source = Vec::new();
    // Fill the signature probe even when a pipe delivers one byte per read.
    // Only identified objects have the object-format bound; source stays source.
    reader
        .by_ref()
        .take((msync::object::MAGIC.len() + 1) as u64)
        .read_to_end(&mut source)
        .map_err(failure)?;
    if msync::object::is_object(&source) {
        let remaining = msync::object::MAX_OBJECT_BYTES.saturating_sub(source.len()) + 1;
        reader
            .take(remaining as u64)
            .read_to_end(&mut source)
            .map_err(failure)?;
        if source.len() > msync::object::MAX_OBJECT_BYTES {
            return Err(
                Issue::new("relocatable object exceeds the 64MiB format bound")
                    .stage(presentation::Stage::Load)
                    .field("object", file)
                    .help("Split the object into smaller modules and recompile it.")
                    .into(),
            );
        }
    } else {
        reader.read_to_end(&mut source).map_err(failure)?;
    }
    Ok(source)
}

fn write_object(output: &str, input: &str, bytes: &[u8]) -> CliResult {
    if output == "-" {
        if io::stdout().is_terminal() {
            return Err(
                Issue::new("object output requires a file or redirected standard output")
                    .stage(presentation::Stage::Compile)
                    .help("Use compile FILE -o module.mobj, or pipe --output - to another command.")
                    .into(),
            );
        }
        let mut writer = io::stdout().lock();
        return writer
            .write_all(bytes)
            .and_then(|()| writer.flush())
            .map_err(|cause| {
                Issue::new("cannot write compiled object to standard output")
                    .stage(presentation::Stage::Compile)
                    .field("cause", cause.to_string())
                    .into()
            });
    }
    let target = Path::new(output);
    if input != "-" {
        let source = Path::new(input);
        let same_name = std::path::absolute(source)
            .ok()
            .zip(std::path::absolute(target).ok())
            .is_some_and(|(source, target)| source == target);
        let same_identity = same_file::is_same_file(source, target).unwrap_or(false);
        if same_name || same_identity {
            return Err(
                Issue::new("compiled object output would replace the input file")
                    .stage(presentation::Stage::Compile)
                    .field("input", input)
                    .field("output", output)
                    .help("Choose a separate object filename; compilation never edits its input.")
                    .into(),
            );
        }
    }
    let failure = |cause: io::Error| {
        CliError::from(
            Issue::new("cannot publish compiled object")
                .stage(presentation::Stage::Compile)
                .field("output", output)
                .field("cause", cause.to_string())
                .help("Choose an ordinary writable output file in an existing directory."),
        )
    };
    if fs::symlink_metadata(target).is_ok_and(|metadata| metadata.permissions().readonly()) {
        return Err(failure(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "existing object output is read-only",
        )));
    }
    let stage = msync::io::StagedFile::new(target, false).map_err(failure)?;
    fs::write(stage.path(), bytes).map_err(failure)?;
    stage.preserve_target_permissions().map_err(failure)?;
    stage.commit().map_err(failure)
}

struct SourceUnit<'a> {
    text: &'a str,
    name: &'a str,
    context: CompileContext,
}

struct ObjectUnit<'a> {
    bytes: &'a [u8],
    name: &'a str,
    context: CompileContext,
}

impl<'a> ObjectUnit<'a> {
    fn file(bytes: &'a [u8], name: &'a str) -> CliResult<Self> {
        let context = CompileContext::capture((name != "-").then(|| Path::new(name)))
            .map_err(|cause| CliError::io(IoAction::WorkingDirectory, None, cause))?;
        Ok(Self {
            bytes,
            name,
            context,
        })
    }
}

impl<'a> SourceUnit<'a> {
    fn annotate(&self, trace: &mut msync::telemetry::SpanGuard) {
        trace.attribute(
            "source.kind",
            if self.context.source_file.is_some() {
                "file"
            } else if self.name == "-" {
                "stdin"
            } else {
                "repl"
            },
        );
        trace.attribute_lazy("source.pwd", || {
            self.context.pwd.to_string_lossy().into_owned()
        });
        if let Some(file) = &self.context.source_file {
            trace.attribute_lazy("source.file", || file.to_string_lossy().into_owned());
        }
    }
    fn file(text: &'a str, name: &'a str) -> CliResult<Self> {
        let file = (name != "-").then(|| Path::new(name));
        Self::new(text, name, file)
    }

    fn repl(text: &'a str) -> CliResult<Self> {
        Self::new(text, "<repl>", None)
    }

    fn new(text: &'a str, name: &'a str, file: Option<&Path>) -> CliResult<Self> {
        let context = CompileContext::capture(file)
            .map_err(|error| CliError::io(IoAction::WorkingDirectory, None, error))?;
        Ok(Self {
            text,
            name,
            context,
        })
    }
}

fn run_file(options: &Args) -> CliResult {
    let file = options
        .file
        .as_deref()
        .ok_or("missing input file; try --help")?;
    if options.command == "fmt" && file == "-" && !options.stdout && !options.check_format {
        return Err("fmt edits a script file; provide its filename, or use --stdout for an explicit preview".into());
    }
    let input = read_input(file)?;
    if msync::object::is_object(&input) {
        if options.command == "fmt" {
            return Err(Issue::new("compiled objects are not source scripts")
                .stage(presentation::Stage::Load)
                .field("object", file)
                .help("Format the original .msync source, then compile a new object.")
                .into());
        }
        let mut state = structured::State::default();
        let outcome = state.submit_object(
            ObjectUnit::file(&input, file)?,
            &options.command,
            options.dry_run,
            options,
            false,
        );
        let closed = state.shutdown(options.color);
        return outcome.and(closed);
    }
    let source = source_text(file, input)?;
    if options.command == "fmt" {
        return format_file(file, &source, options);
    }
    language::select_version(&source, options.syntax, Version::Structured)
        .map_err(|errors| diagnostics(file, &source, &errors, options.color))?;
    let mut state = structured::State::default();
    let outcome = state.submit(
        SourceUnit::file(&source, file)?,
        &options.command,
        options.dry_run,
        options,
        false,
    );
    let closed = state.shutdown(options.color);
    outcome.and(closed)
}

fn format_file(file: &str, source: &str, options: &Args) -> CliResult {
    let mut trace = msync::telemetry::span("format_file");
    let result = format_file_inner(file, source, options);
    trace.finish_result(&result);
    result
}

fn format_file_inner(file: &str, source: &str, options: &Args) -> CliResult {
    language::select_version(source, options.syntax, Version::Structured)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    let formatted = language::format(source)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    if options.check_format {
        return if formatted == source {
            Ok(())
        } else {
            Err(Issue::new("formatting differs")
                .field("path", file)
                .help(format!("Run msync fmt {file:?} to update this script, or use --stdout to inspect the formatted text.")).into())
        };
    }
    if options.stdout {
        return print_text(&formatted);
    }
    if formatted == source {
        return Ok(());
    }
    write_formatted(file, &formatted)
}

fn write_formatted(file: &str, formatted: &str) -> CliResult {
    if file == "-" {
        return Err("fmt --write requires a file, not stdin".into());
    }
    let path = Path::new(file);
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| CliError::io(IoAction::WriteScript, Some(path), error))?;
    if metadata.file_type().is_symlink() {
        return Err(Issue::new("fmt --write refuses symlinks")
            .field("path", file)
            .help("Name the actual script file, or use --stdout for a read-only preview.")
            .into());
    }
    if metadata.permissions().readonly() {
        return Err(Issue::new("refusing to format a read-only file")
            .field("path", file)
            .help("Use fmt FILE --stdout to preview, or make a writable copy of this script.")
            .into());
    }
    let temporary = path.with_file_name(format!(
        ".{}.{}.fmt.tmp",
        path.file_name().unwrap_or_default().to_string_lossy(),
        std::process::id()
    ));
    let mut output = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| {
            let hint = match error.kind() {
                io::ErrorKind::AlreadyExists => "The existing stage is never overwritten. Inspect it before retrying; it may contain recoverable data.",
                io::ErrorKind::PermissionDenied => "Check write permissions on the script directory, or use --stdout for a read-only preview.",
                io::ErrorKind::StorageFull => "Free space on this volume before formatting, or use --stdout for a read-only preview.",
                _ => "Check that the script directory is writable, or use --stdout for a read-only preview.",
            };
            Issue::new("cannot create formatting stage")
            .field("path", file)
            .field("stage", temporary.to_string_lossy())
            .field("cause", error.to_string())
            .help(hint)
        })?;
    let result = (|| -> io::Result<()> {
        output.write_all(formatted.as_bytes())?;
        output.sync_all()?;
        fs::set_permissions(&temporary, metadata.permissions())?;
        drop(output);
        fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result.map_err(|error| CliError::io(IoAction::WriteScript, Some(path), error))
}

fn diagnostics(file: &str, source: &str, errors: &[Report], color: Color) -> CliError {
    CliError::Presented(
        errors
            .iter()
            .map(|error| syntax::render_report_named(source, error, file, color.stderr()))
            .collect::<Vec<_>>()
            .join("\n\n"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn spool_limit_is_a_per_program_optional_nonnegative_byte_budget() {
        let parse = |args: &[&str]| arguments(args.iter().map(|arg| (*arg).to_owned()));
        assert_eq!(parse(&["repl"]).unwrap().spool_limit, None);
        assert_eq!(
            parse(&["repl", "--spool-limit", "0"]).unwrap().spool_limit,
            Some(0)
        );
        assert_eq!(
            parse(&["repl", "--spool-limit", "2KiB"])
                .unwrap()
                .spool_limit,
            Some(2048)
        );
        assert!(parse(&["repl", "--spool-limit", "-1"]).is_err());
        assert!(parse(&["repl", "--spool-limit"]).is_err());
    }

    #[test]
    fn object_output_is_explicit_and_only_valid_for_compilation() {
        let parse = |arguments: &[&str]| {
            super::arguments(arguments.iter().map(|argument| (*argument).to_owned()))
        };
        let options = parse(&["compile", "input.msync", "-o", "module.mobj"]).unwrap();
        assert_eq!(options.output.as_deref(), Some("module.mobj"));
        assert_eq!(options.file.as_deref(), Some("input.msync"));
        assert!(parse(&["run", "input.msync", "-o", "module.mobj"]).is_err());
        assert!(parse(&["compile", "input.msync", "--output"]).is_err());
        assert!(parse(&["compile", "input.msync", "-o", "a", "--output", "b"]).is_err());
        assert!(parse(&["compile", "input.msync", "-o", "a", "--dry-run"]).is_err());
        assert_eq!(
            parse(&["compile", "-", "--output", "-"])
                .unwrap()
                .output
                .as_deref(),
            Some("-")
        );
    }

    #[test]
    fn object_export_never_replaces_its_source_or_a_readonly_output() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let workspace = tempfile::tempdir_in(root).unwrap();
        let source = workspace.path().join("input.msync");
        let output = workspace.path().join("output.mobj");
        fs::write(&source, "source -> target\n").unwrap();
        assert!(
            write_object(
                source.to_str().unwrap(),
                source.to_str().unwrap(),
                b"object"
            )
            .is_err()
        );
        assert_eq!(fs::read_to_string(&source).unwrap(), "source -> target\n");
        write_object(
            output.to_str().unwrap(),
            source.to_str().unwrap(),
            b"first object",
        )
        .unwrap();
        assert_eq!(fs::read(&output).unwrap(), b"first object");
        let writable = fs::metadata(&output).unwrap().permissions();
        let mut readonly = writable.clone();
        readonly.set_readonly(true);
        fs::set_permissions(&output, readonly).unwrap();
        assert!(
            write_object(
                output.to_str().unwrap(),
                source.to_str().unwrap(),
                b"replacement"
            )
            .is_err()
        );
        assert_eq!(fs::read(&output).unwrap(), b"first object");
        fs::set_permissions(&output, writable).unwrap();
        assert_eq!(fs::read_dir(workspace.path()).unwrap().count(), 2);
    }

    #[test]
    fn object_signature_probe_does_not_misclassify_short_source_pipe_reads() {
        struct OneByte<'a>(&'a [u8]);
        impl Read for OneByte<'_> {
            fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
                let length = self.0.len().min(output.len()).min(1);
                output[..length].copy_from_slice(&self.0[..length]);
                self.0 = &self.0[length..];
                Ok(length)
            }
        }
        for bytes in [
            b"MSYNC-OBJECT -> target\n".as_slice(),
            b"MSYNC-OBJECT\n{}".as_slice(),
        ] {
            assert_eq!(read_content(&mut OneByte(bytes), "-").unwrap(), bytes);
        }
    }

    #[test]
    fn formatter_never_removes_a_temporary_file_it_did_not_create() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let directory = tempfile::tempdir_in(root).unwrap();
        let input = directory.path().join("script.msync");
        fs::write(&input, "a->b").unwrap();
        let occupied = directory
            .path()
            .join(format!(".script.msync.{}.fmt.tmp", std::process::id()));
        fs::write(&occupied, "recoverable data").unwrap();
        assert!(write_formatted(input.to_str().unwrap(), "a -> b\n").is_err());
        assert_eq!(fs::read_to_string(occupied).unwrap(), "recoverable data");
        assert_eq!(fs::read_to_string(input).unwrap(), "a->b");
    }
}
