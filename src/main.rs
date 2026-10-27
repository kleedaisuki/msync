use std::env;
use std::fs;
use std::io::{self, BufRead, IsTerminal, Read, Write};
use std::path::Path;
use std::process::ExitCode;
use std::thread::{self, JoinHandle};

use msync::compiler::{CompileContext, Compiler};
use msync::plan::{FileClass, Plan};
use msync::scheduler::{self, ResourceLimits};
use msync::syntax::{self, Report, Script, StatementKind};
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

REPL: /help or /? for commands. No arguments starts the REPL.
Paths containing whitespace or '#' may be double quoted.
Use $pwd::pwd/file for working-directory paths; $file::this and $file::parent identify the source script.
Pipes and sockets end independently; Ctrl-C cancels active streams.
";

const REPL_HELP: &str = "REPL commands
  /help, /?                Show this help
  /quit, /exit              Exit (or Ctrl-D / Ctrl-Z + Enter)
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
                    && env::var("TERM").as_deref() != Ok("dumb")
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
                    && env::var("TERM").as_deref() != Ok("dumb")
            }
        }
    }
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

fn arguments() -> Result<Args, String> {
    let mut options = Args::default();
    let mut args = env::args().skip(1);
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
                return Err(format!("unknown option '{value}'; try --help"));
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
    let result = arguments().and_then(run);
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            if !message.is_empty() {
                print_error(&message);
            }
            ExitCode::FAILURE
        }
    }
}

fn run(options: Args) -> Result<(), String> {
    if let Some(path) = &options.trace {
        msync::telemetry::init(path)?;
    }
    let mut trace = msync::telemetry::span("session");
    let result = dispatch(&options);
    if result.is_err() {
        trace.error();
    }
    drop(trace);
    let flushed = msync::telemetry::flush();
    match (result, flushed) {
        (Err(error), Err(export_error)) => Err(format!("{error}\nwarning: {export_error}")),
        (Err(error), _) => Err(error),
        (Ok(()), outcome) => outcome,
    }
}

fn dispatch(options: &Args) -> Result<(), String> {
    match options.command.as_str() {
        "help" => print_help(HELP, options.color),
        "version" => print_text(concat!("msync ", env!("CARGO_PKG_VERSION"), "\n")),
        "repl" if options.file.is_none() => repl(options),
        "repl" => Err("repl does not take a file; use /load inside the REPL".into()),
        _ => run_file(options),
    }
}

fn print_text(text: &str) -> Result<(), String> {
    anstream::AutoStream::new(io::stdout().lock(), anstream::ColorChoice::Always)
        .write_all(text.as_bytes())
        .map_err(|error| error.to_string())
}

fn print_error(message: &str) {
    let _ = writeln!(
        anstream::AutoStream::new(io::stderr().lock(), anstream::ColorChoice::Always),
        "{message}"
    );
}

fn print_help(help: &str, color: Color) -> Result<(), String> {
    if !color.stdout() {
        return print_text(help);
    }
    let mut output = String::with_capacity(help.len() + 256);
    for line in help.lines() {
        if line.starts_with("  ") {
            let content = line.trim_start();
            let end = content.find("  ").unwrap_or(content.len());
            output.push_str(&format!(
                "  \x1b[1;36m{}\x1b[0m{}\n",
                &content[..end],
                &content[end..]
            ));
        } else if !line.is_empty() {
            output.push_str(&format!("\x1b[1;33m{line}\x1b[0m\n"));
        } else {
            output.push('\n');
        }
    }
    print_text(&output)
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

fn read_source(file: &str) -> Result<String, String> {
    let mut trace = msync::telemetry::span("read");
    let result = read_source_inner(file);
    trace.finish_result(&result);
    if let Ok(source) = &result {
        trace.metric("bytes", source.len() as u64);
    }
    result
}

fn read_source_inner(file: &str) -> Result<String, String> {
    if file != "-" {
        return fs::read_to_string(file).map_err(|error| format!("{file}: {error}"));
    }
    let mut source = String::new();
    io::stdin()
        .read_to_string(&mut source)
        .map_err(|error| error.to_string())?;
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
    fn file(text: &'a str, name: &'a str) -> Result<Self, String> {
        let file = (name != "-").then(|| Path::new(name));
        Self::new(text, name, file)
    }

    fn repl(text: &'a str) -> Result<Self, String> {
        Self::new(text, "<repl>", None)
    }

    fn new(text: &'a str, name: &'a str, file: Option<&Path>) -> Result<Self, String> {
        let context = CompileContext::capture(file)
            .map_err(|error| format!("cannot capture source context: {error}"))?;
        Ok(Self {
            text,
            name,
            context,
        })
    }
}

fn run_file(options: &Args) -> Result<(), String> {
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

fn format_file(file: &str, source: &str, options: &Args) -> Result<(), String> {
    let mut trace = msync::telemetry::span("format_file");
    let result = format_file_inner(file, source, options);
    trace.finish_result(&result);
    result
}

fn format_file_inner(file: &str, source: &str, options: &Args) -> Result<(), String> {
    let formatted = syntax::format_rich(source)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    if options.check_format {
        return if formatted == source {
            Ok(())
        } else {
            Err(format!(
                "{file}: formatting differs; run 'msync fmt {file}' to update the script"
            ))
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

fn write_formatted(file: &str, formatted: &str) -> Result<(), String> {
    if file == "-" {
        return Err("fmt --write requires a file, not stdin".into());
    }
    let path = Path::new(file);
    let metadata = fs::symlink_metadata(path).map_err(|error| error.to_string())?;
    if metadata.file_type().is_symlink() {
        return Err("fmt --write refuses symlinks; name the actual file".into());
    }
    if metadata.permissions().readonly() {
        return Err(format!("{file}: refusing to format a read-only file"));
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
        .map_err(|error| format!("{file}: cannot create formatting temporary: {error}"))?;
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
    result.map_err(|error| format!("{file}: {error}"))
}

fn submit(
    input: SourceUnit<'_>,
    compiler: &mut Compiler,
    command: &str,
    dry_run: bool,
    color: Color,
    limits: &ResourceLimits,
) -> Result<(), String> {
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
            return print_text("ok\n");
        }
        let plan = prepare(&program, &script, !dry_run)
            .map_err(|error| diagnostics(file, source, &[runtime_report(&script, error)], color))?;
        *compiler = candidate;
        if matches!(command, "plan" | "compile") {
            return print_text(&plan.to_string());
        }
        if dry_run {
            print_text(&plan.to_string())?;
            return print_text(&format!(
                "preview: {} operation(s); no files written\n",
                plan.nodes.len()
            ));
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
) -> Result<(), String> {
    let report = summary.report;
    for action in report.actions {
        print_text(&format!("{action}\n"))?;
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
        format!("\x1b[32m{status}\x1b[0m")
    } else {
        status
    })
}

fn diagnostics(file: &str, source: &str, errors: &[Report], color: Color) -> String {
    let name = file.escape_debug();
    errors
        .iter()
        .map(|error| {
            let rendered = syntax::render_report(source, error, color.stderr());
            if color.stderr() {
                format!("\x1b[1;31merror\x1b[0m in \x1b[1;34m{name}\x1b[0m\n{rendered}")
            } else {
                format!("error in {name}\n{rendered}")
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn runtime_report(script: &Script, error: msync::runtime::RuntimeError) -> Report {
    let mut report = Report::new(error.message, error.span);
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
        "Use msync plan FILE to inspect the resolved endpoints and dependencies before retrying.",
    )
}

struct ActiveJob {
    id: u64,
    handle: Option<JoinHandle<Result<scheduler::ExecutionSummary, Report>>>,
    cancel: CancellationToken,
    source: String,
    file: String,
    removed: usize,
}

impl ActiveJob {
    fn finished(&self) -> bool {
        self.handle.as_ref().is_none_or(JoinHandle::is_finished)
    }

    fn join(mut self, stop: bool, color: Color) -> Result<(), String> {
        if stop {
            self.cancel.cancel();
        }
        let result = self
            .handle
            .take()
            .ok_or("background job was already joined")?
            .join()
            .map_err(|_| {
                "background worker panicked; its resources have been joined".to_string()
            })?;
        if stop {
            return print_text(&format!("job {}: stopped\n", self.id));
        }
        let summary =
            result.map_err(|report| diagnostics(&self.file, &self.source, &[report], color))?;
        print_text(&format!("job {}: completed\n", self.id))?;
        print_summary(summary, self.removed, true, color)
    }
}

impl Drop for ActiveJob {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

struct ReplState {
    compiler: Compiler,
    dry_run: bool,
    active: Option<ActiveJob>,
    next_job: u64,
}

impl ReplState {
    fn join(&mut self, stop: bool, color: Color) -> Result<(), String> {
        match self.active.take() {
            Some(job) => job.join(stop, color),
            None => print_text("no active jobs\n"),
        }
    }

    fn reap(&mut self, color: Color) {
        if self.active.as_ref().is_some_and(ActiveJob::finished)
            && let Err(message) = self.join(false, color)
        {
            print_error(&message);
        }
    }
}

fn repl_submit(input: SourceUnit<'_>, state: &mut ReplState, options: &Args) -> Result<(), String> {
    let mut trace = msync::telemetry::span("submission");
    input.annotate(&mut trace);
    let result = repl_submit_inner(input, state, options);
    trace.finish_result(&result);
    result
}

fn repl_submit_inner(
    input: SourceUnit<'_>,
    state: &mut ReplState,
    options: &Args,
) -> Result<(), String> {
    let SourceUnit {
        text: source,
        name: file,
        context,
    } = input;
    let script = syntax::parse_rich(source)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    let copies = script
        .statements
        .iter()
        .any(|statement| matches!(statement.kind, StatementKind::Copy { .. }));
    if copies && state.active.is_some() {
        return Err(
            "a background plan is active; use /wait or /stop before submitting another copy plan"
                .into(),
        );
    }
    let mut candidate = state.compiler.clone();
    let program = candidate
        .compile_rich_with_context(&script, &context)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    let plan = prepare(&program, &script, !state.dry_run).map_err(|error| {
        diagnostics(
            file,
            source,
            &[runtime_report(&script, error)],
            options.color,
        )
    })?;
    if !copies {
        state.compiler = candidate;
        return Ok(());
    }
    if state.dry_run {
        print_text(&plan.to_string())?;
        if copies {
            print_text(&format!(
                "preview: {} operation(s); no files written\n",
                plan.nodes.len()
            ))?;
        }
        state.compiler = candidate;
        return Ok(());
    }
    let streaming = plan.nodes.iter().any(|node| {
        node.kinds.source == FileClass::Stream || node.kinds.destination == FileClass::Stream
    });
    if !streaming {
        let mut execution = msync::telemetry::span("execute");
        let result = scheduler::execute_plan(&plan, &options.limits);
        execution.finish_result(&result);
        state.compiler = candidate;
        return print_summary(
            result.map_err(|error| {
                diagnostics(
                    file,
                    source,
                    &[runtime_report(&script, error)],
                    options.color,
                )
            })?,
            plan.removed.len(),
            copies,
            options.color,
        );
    }
    let cancel = CancellationToken::new();
    let worker_cancel = cancel.clone();
    let limits = options.limits.clone();
    let parent = msync::telemetry::context();
    let removed = plan.removed.len();
    let handle = thread::Builder::new()
        .name("msync-repl-plan".into())
        .spawn(move || {
            let mut execution = msync::telemetry::linked_span("execute", parent.as_ref());
            let context = execution.context();
            let result = msync::telemetry::with_context(context.as_ref(), || {
                scheduler::execute_plan_cancellable(&plan, &limits, worker_cancel)
            });
            execution.finish_result(&result);
            result.map_err(|error| runtime_report(&script, error))
        })
        .map_err(|error| format!("cannot start background plan: {error}"))?;
    let id = state.next_job;
    state.next_job += 1;
    state.active = Some(ActiveJob {
        id,
        handle: Some(handle),
        cancel,
        source: source.to_owned(),
        file: file.to_owned(),
        removed,
    });
    state.compiler = candidate;
    print_text(&format!(
        "job {id}: running in background; use /jobs, /wait or /stop\n"
    ))
}

fn repl(options: &Args) -> Result<(), String> {
    let mut trace = msync::telemetry::span("repl");
    let result = repl_inner(options);
    trace.finish_result(&result);
    result
}

fn repl_inner(options: &Args) -> Result<(), String> {
    let interactive = io::stdin().is_terminal();
    if interactive {
        print_text("msync — /help for commands; /quit to exit\n")?;
    }
    let mut state = ReplState {
        compiler: Compiler::new(),
        dry_run: options.dry_run,
        active: None,
        next_job: 1,
    };
    let stdin = io::stdin();
    let mut reader = stdin.lock();
    let mut line = String::new();
    loop {
        state.reap(options.color);
        if interactive {
            print_text(if options.color.stdout() {
                "\x1b[1;36mmsync> \x1b[0m"
            } else {
                "msync> "
            })?;
            io::stdout().flush().map_err(|error| error.to_string())?;
        }
        line.clear();
        if reader
            .read_line(&mut line)
            .map_err(|error| error.to_string())?
            == 0
        {
            if state.active.is_some() {
                return state.join(interactive, options.color);
            }
            return Ok(());
        }
        state.reap(options.color);
        let input = line.trim();
        if input.is_empty() {
            continue;
        }
        if matches!(input, "/quit" | "/exit" | ":quit" | ":q") {
            if state.active.is_some() {
                return state.join(true, options.color);
            }
            return Ok(());
        }
        let result = if is_repl_command(input) {
            repl_command(input, &mut state, options)
        } else {
            repl_submit(SourceUnit::repl(&line)?, &mut state, options)
        };
        if let Err(message) = result {
            print_error(&message);
        }
    }
}

fn is_repl_command(input: &str) -> bool {
    // Absolute POSIX paths remain valid DSL operands.
    (input.starts_with('/') || input.starts_with(':'))
        && !input.contains("->")
        && !input.contains("=>")
        && !input.contains(":=")
}

fn repl_command(input: &str, state: &mut ReplState, options: &Args) -> Result<(), String> {
    let mut trace = msync::telemetry::span("repl_command");
    let result = repl_command_inner(input, state, options);
    trace.finish_result(&result);
    result
}

fn repl_command_inner(input: &str, state: &mut ReplState, options: &Args) -> Result<(), String> {
    let (command, argument) = input.split_once(char::is_whitespace).unwrap_or((input, ""));
    let argument = argument.trim().trim_matches('"');
    let name = command.trim_start_matches(['/', ':']);
    if state.active.is_some() && matches!(name, "cd" | "fmt") {
        return Err(format!(
            "{command} changes shared working-directory or filesystem state; use /wait or /stop before this command"
        ));
    }
    match name {
        "jobs" => match &state.active {
            Some(job) => print_text(&format!("job {}: running\n", job.id)),
            None => print_text("no active jobs\n"),
        },
        "wait" => state.join(false, options.color),
        "stop" => state.join(true, options.color),
        "help" | "?" => print_help(REPL_HELP, options.color),
        "limits" => print_text(&format!(
            "jobs={}, max-streams={}, buffer-size={} bytes, memory-limit={} bytes\n",
            options.limits.jobs,
            options.limits.max_streams,
            options.limits.buffer_bytes,
            options.limits.memory_bytes
        )),
        "bindings" => print_text(&format!("{}\n", state.compiler.bindings().join("\n"))),
        "reset" => {
            state.compiler = Compiler::new();
            print_text("bindings cleared\n")
        }
        "pwd" => print_text(&format!(
            "{}\n",
            env::current_dir()
                .map_err(|error| error.to_string())?
                .display()
        )),
        "cd" => {
            if argument.is_empty() {
                return Err("usage: /cd PATH".into());
            }
            env::set_current_dir(argument).map_err(|error| format!("{argument}: {error}"))
        }
        "dry-run" => {
            match argument {
                "on" => state.dry_run = true,
                "off" => state.dry_run = false,
                "" => {}
                _ => return Err("usage: /dry-run [on|off]".into()),
            }
            print_text(if state.dry_run {
                "dry-run: on\n"
            } else {
                "dry-run: off\n"
            })
        }
        "load" | "check" | "plan" | "fmt" => {
            if argument.is_empty() || argument == "-" {
                return Err(format!("usage: {command} FILE (not stdin)"));
            }
            let source = read_source(argument)?;
            match command.trim_start_matches(['/', ':']) {
                "fmt" => format_file(argument, &source, options),
                "load" => repl_submit(SourceUnit::file(&source, argument)?, state, options),
                mode => submit(
                    SourceUnit::file(&source, argument)?,
                    &mut state.compiler.clone(),
                    if mode == "plan" { "compile" } else { "check" },
                    state.dry_run,
                    options.color,
                    &options.limits,
                ),
            }
        }
        _ => Err(format!("unknown REPL command '{command}'; try /help")),
    }
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
