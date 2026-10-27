mod editor;
mod presentation;
mod repl;
mod shell;

use std::env;
use std::fs;
use std::io::{self, IsTerminal, Read, Write};
use std::path::Path;
use std::process::ExitCode;
use std::thread::{self, JoinHandle};

use msync::compiler::{CompileContext, Compiler};
use msync::plan::{FileClass, Plan};
use msync::scheduler::{self, ResourceLimits};
use msync::syntax::{self, Report, Script, StatementKind};
use presentation::{CliError, CliResult, IoAction, Issue};
use tokio_util::sync::CancellationToken;

const HELP: &str = "msync — a compiled file synchronization DSL

USAGE
  msync [run] FILE [--dry-run]     Execute a script ('-' reads stdin)
  msync check FILE                Parse and compile without changing files
  msync plan FILE                 Show the optimized dependency graph (metadata only)
  msync compile FILE              Alias for plan (never copies file data)
  msync fmt FILE                  Format the script file in place
  msync repl [--dry-run]          Start a persistent interactive session

OPTIONS
  -n, --dry-run       Validate and preview without writing files
  --jobs N           Maximum concurrent finite copies (default: 4)
  --max-streams N    Maximum background pipe/socket flows (default: 16)
  --buffer-size N    Buffer bytes per flow (default: 64KiB)
  --memory-limit N   Aggregate payload-buffer budget (default: 64MiB; not RSS)
  --check            Check formatting without modifying the file (fmt only)
  --stdout           Explicit formatting preview, never writes the file (fmt only)
  --color auto|always|never   Color policy (default: auto; honors NO_COLOR)
  --trace FILE        Write private, local JSONL telemetry ('-' = stderr)
  -h, --help          Show this help
  -V, --version       Show version

EXAMPLES
  msync check sync.msync          Check syntax and bindings before copying
  msync plan sync.msync           Inspect paths, types and optimized dependencies
  msync run sync.msync --dry-run  Preview without transfers or remote probes
  msync fmt sync.msync --check    Check script formatting without rewriting

REPL: /help or /? for commands. No arguments starts the REPL.
Paths containing whitespace or '#' may be double quoted.
Use $pwd::pwd/file for working-directory paths; $file::this and $file::parent identify the source script.
Pipes and sockets end independently; Ctrl-C cancels active streams.
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
  /plan FILE               Print a resolved plan without executing it
  /fmt FILE                Format a script file in place (never executes it)
  /limits                  Show copy, stream and buffer resource limits
  /jobs                    Show the single active background plan
  /wait                    Wait for the active plan to finish
  /stop                    Cancel and join the active plan
Enter DSL statements directly; bindings persist across submissions.
Stream plans run in the background; /wait or /stop before another copy plan.
In supported terminals, Tab completes commands/namespaces and Up/Down browse history.
Ctrl-C clears the edit and stops an active plan; Ctrl-D exits safely.
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

#[derive(Default)]
struct Args {
    command: String,
    file: Option<String>,
    dry_run: bool,
    write: bool,
    stdout: bool,
    check_format: bool,
    color: Color,
    trace: Option<String>,
    limits: ResourceLimits,
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
                        "--check",
                        "--stdout",
                        "--write",
                        "--color",
                        "--trace",
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
    options.limits.validate()?;
    Ok(options)
}

fn add_positional(options: &mut Args, arg: String) -> Result<(), String> {
    if options.command.is_empty() {
        if matches!(
            arg.as_str(),
            "run" | "check" | "plan" | "compile" | "fmt" | "repl"
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

fn print_plan(plan: &Plan, color: Color) -> CliResult {
    if io::stdout().is_terminal() {
        return print_text(&presentation::plan(plan, color.stdout()));
    }
    if !color.stdout() {
        return print_text(&plan.to_string());
    }
    for line in plan.to_string().lines() {
        print_text(&msync::highlight::highlight(
            &presentation::escape(line),
            true,
        ))?;
        print_text("\n")?;
    }
    Ok(())
}

fn print_limits(limits: &ResourceLimits, color: Color) -> CliResult {
    if io::stdout().is_terminal() {
        return print_text(&presentation::limits(limits, color.stdout()));
    }
    print_status(
        &format!(
            "limits: jobs={}, max-streams={}, buffer-size={} bytes, memory-limit={} bytes",
            limits.jobs, limits.max_streams, limits.buffer_bytes, limits.memory_bytes
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
    let end = value
        .find(|ch: char| !ch.is_ascii_digit())
        .unwrap_or(value.len());
    let number = positive(&value[..end])?;
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

fn read_source(file: &str) -> CliResult<String> {
    let mut trace = msync::telemetry::span("read");
    let result = read_source_inner(file);
    trace.finish_result(&result);
    if let Ok(source) = &result {
        trace.metric("bytes", source.len() as u64);
    }
    result
}

fn read_source_inner(file: &str) -> CliResult<String> {
    if file != "-" {
        return fs::read_to_string(file)
            .map_err(|error| CliError::io(IoAction::ReadScript, Some(Path::new(file)), error));
    }
    let mut source = String::new();
    io::stdin()
        .read_to_string(&mut source)
        .map_err(|error| CliError::io(IoAction::ReadInput, None, error))?;
    Ok(source)
}

struct SourceUnit<'a> {
    text: &'a str,
    name: &'a str,
    context: CompileContext,
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
    let source = read_source(file)?;
    if options.command == "fmt" {
        return format_file(file, &source, options);
    }
    submit(
        SourceUnit::file(&source, file)?,
        &mut Compiler::new(),
        &options.command,
        options.dry_run,
        options.color,
        &options.limits,
    )
}

fn format_file(file: &str, source: &str, options: &Args) -> CliResult {
    let mut trace = msync::telemetry::span("format_file");
    let result = format_file_inner(file, source, options);
    trace.finish_result(&result);
    result
}

fn format_file_inner(file: &str, source: &str, options: &Args) -> CliResult {
    let formatted = syntax::format_rich(source)
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

fn submit(
    input: SourceUnit<'_>,
    compiler: &mut Compiler,
    command: &str,
    dry_run: bool,
    color: Color,
    limits: &ResourceLimits,
) -> CliResult {
    let mut trace = msync::telemetry::span("submission");
    input.annotate(&mut trace);
    let SourceUnit {
        text: source,
        name: file,
        context,
    } = input;
    let result = (|| {
        let script = syntax::parse_rich(source)
            .map_err(|errors| diagnostics(file, source, &errors, color))?;
        let mut candidate = compiler.clone();
        let program = candidate
            .compile_rich_with_context(&script, &context)
            .map_err(|errors| diagnostics(file, source, &errors, color))?;
        if command == "check" {
            *compiler = candidate;
            if !io::stdout().is_terminal() {
                return print_text(&format!(
                    "{}\n",
                    presentation::paint("ok", "1;32", color.stdout())
                ));
            }
            return print_status(
                &format!(
                    "ok: {} checked; no files written",
                    presentation::escape(file)
                ),
                color,
            );
        }
        let plan = prepare(&program, &script, !dry_run)
            .map_err(|error| diagnostics(file, source, &[runtime_report(&script, error)], color))?;
        *compiler = candidate;
        if matches!(command, "plan" | "compile") {
            return print_plan(&plan, color);
        }
        if dry_run {
            print_plan(&plan, color)?;
            return print_status(
                &format!(
                    "preview: {} operation(s); no files written\n",
                    plan.nodes.len()
                ),
                color,
            );
        }
        let mut execution = msync::telemetry::span("execute");
        let result = scheduler::execute_plan(&plan, limits);
        execution.finish_result(&result);
        let summary = result
            .map_err(|error| diagnostics(file, source, &[runtime_report(&script, error)], color))?;
        print_summary(
            summary,
            plan.removed.len(),
            !program.operations.is_empty(),
            color,
        )
    })();
    trace.finish_result(&result);
    result
}

fn prepare(
    program: &msync::compiler::Program,
    script: &Script,
    live: bool,
) -> Result<Plan, msync::runtime::RuntimeError> {
    let operands = script
        .statements
        .iter()
        .filter(|statement| matches!(statement.kind, StatementKind::Copy { .. }))
        .map(|statement| {
            statement
                .operands
                .as_ref()
                .map(|operands| (operands.left.content, operands.right.content))
                .unwrap_or((statement.span, statement.span))
        })
        .collect::<Vec<_>>();
    scheduler::prepare_spanned(program, live, &operands)
}

fn print_summary(
    summary: scheduler::ExecutionSummary,
    removed: usize,
    show: bool,
    color: Color,
) -> CliResult {
    let report = summary.report;
    for action in report.actions {
        print_text(&msync::highlight::highlight(
            &presentation::escape(&action),
            color.stdout(),
        ))?;
        print_text("\n")?;
    }
    if !show {
        return Ok(());
    }
    let status = format!(
        "done: {} operation(s), {} eliminated, {} file(s), {} directories, {} flow bytes\n",
        summary.completed,
        removed,
        report.copied_files,
        report.created_directories,
        report.transferred_bytes
    );
    print_text(&if color.stdout() {
        format!(
            "\x1b[1;32mdone:\x1b[0m{}",
            status.strip_prefix("done:").unwrap_or(&status)
        )
    } else {
        status
    })
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

fn runtime_report(script: &Script, error: msync::runtime::RuntimeError) -> Report {
    let (title, detail) = error
        .message
        .split_once('\n')
        .unwrap_or((&error.message, ""));
    let mut report = Report::new(title, error.span);
    if !detail.is_empty() {
        report = report.help(format!("Runtime details:\n{detail}"));
    }
    if let Some(statement) = script.statements.iter().find(|statement| {
        statement.span.start <= error.span.start && statement.span.end >= error.span.end
    }) && let Some(operands) = &statement.operands
        && error.span == statement.span
    {
        report.diagnostic.span = operands.operator;
        report = report
            .label(operands.left.content, "source of this transfer")
            .label(operands.right.content, "destination of this transfer");
    }
    report.help(
        "Inspect the marked source and destination. Use msync plan FILE for types/dependencies, or --trace FILE for operation details before retrying.",
    )
}

#[cfg(test)]
mod tests {
    use super::*;

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
