use std::env;
use std::fs;
use std::io::{self, BufRead, IsTerminal, Read, Write};
use std::path::Path;
use std::process::ExitCode;

use msync::compiler::Compiler;
use msync::runtime::{ExecutionOptions, execute};
use msync::syntax::{self, Diagnostic};

const HELP: &str = "msync — a compiled file synchronization DSL

USAGE
  msync [run] FILE [--dry-run]     Execute a script ('-' reads stdin)
  msync check FILE                Parse and compile without changing files
  msync compile FILE              Print the resolved execution plan
  msync fmt FILE [--check|--write] Format, check, or update a script
  msync repl [--dry-run]          Start a persistent interactive session

OPTIONS
  -n, --dry-run       Validate and preview without writing files
  --color auto|always|never   Color policy (default: auto; honors NO_COLOR)
  --trace FILE        Write private, local JSONL telemetry ('-' = stderr)
  -h, --help          Show this help
  -V, --version       Show version

REPL: /help or /? for commands. No arguments starts the REPL.
Paths containing whitespace or '#' may be double quoted.
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
  /fmt FILE                Print a file in canonical form
Enter DSL statements directly; bindings persist across submissions.
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
    check_format: bool,
    color: Color,
    trace: Option<String>,
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
            "--check" => options.check_format = true,
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
    if (options.write || options.check_format) && options.command != "fmt" {
        return Err("--write and --check are only valid with fmt".into());
    }
    if options.write && options.check_format {
        return Err("--write and --check are mutually exclusive".into());
    }
    Ok(options)
}

fn add_positional(options: &mut Args, arg: String) -> Result<(), String> {
    if options.command.is_empty() {
        if matches!(arg.as_str(), "run" | "check" | "compile" | "fmt" | "repl") {
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
        "help" => print_text(HELP),
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

fn run_file(options: &Args) -> Result<(), String> {
    let file = options
        .file
        .as_deref()
        .ok_or("missing input file; try --help")?;
    let source = read_source(file)?;
    if options.command == "fmt" {
        return format_file(file, &source, options);
    }
    submit(
        &source,
        file,
        &mut Compiler::new(),
        &options.command,
        options.dry_run,
        options.color,
    )
}

fn format_file(file: &str, source: &str, options: &Args) -> Result<(), String> {
    let mut trace = msync::telemetry::span("format_file");
    let result = format_file_inner(file, source, options);
    trace.finish_result(&result);
    result
}

fn format_file_inner(file: &str, source: &str, options: &Args) -> Result<(), String> {
    let formatted = syntax::format(source)
        .map_err(|errors| diagnostics(file, source, &errors, options.color))?;
    if options.check_format {
        return if formatted == source {
            Ok(())
        } else {
            Err(format!(
                "{file}: formatting differs; run 'msync fmt --write'"
            ))
        };
    }
    if options.write {
        return write_formatted(file, &formatted);
    }
    print_text(&formatted)
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
    source: &str,
    file: &str,
    compiler: &mut Compiler,
    command: &str,
    dry_run: bool,
    color: Color,
) -> Result<(), String> {
    let mut trace = msync::telemetry::span("submission");
    let result = (|| {
        let script =
            syntax::parse(source).map_err(|errors| diagnostics(file, source, &errors, color))?;
        let program = compiler
            .compile(&script)
            .map_err(|errors| diagnostics(file, source, &errors, color))?;
        match command {
            "check" => print_text("ok\n"),
            "compile" => print_text(&program.to_string()),
            _ => {
                let report = execute(&program, &ExecutionOptions { dry_run }).map_err(|error| {
                    diagnostics(
                        file,
                        source,
                        &[Diagnostic {
                            message: error.message,
                            span: error.span,
                        }],
                        color,
                    )
                })?;
                for action in report.actions {
                    print_text(&format!("{action}\n"))?;
                }
                if !program.operations.is_empty() {
                    let status = if dry_run {
                        format!(
                            "preview: {} operation(s); no files written\n",
                            program.operations.len()
                        )
                    } else {
                        format!(
                            "done: {} file(s), {} directories\n",
                            report.copied_files, report.created_directories
                        )
                    };
                    if color.stdout() {
                        print_text(&format!("\x1b[32m{status}\x1b[0m"))?;
                    } else {
                        print_text(&status)?;
                    }
                }
                Ok(())
            }
        }
    })();
    if result.is_err() {
        trace.error();
    }
    result
}

fn diagnostics(file: &str, source: &str, errors: &[Diagnostic], color: Color) -> String {
    errors
        .iter()
        .map(|error| {
            let rendered = syntax::render_diagnostic(source, error);
            if color.stderr() {
                format!("\x1b[1;31merror\x1b[0m in \x1b[1;34m{file}\x1b[0m\n{rendered}")
            } else {
                format!("error in {file}\n{rendered}")
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
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
    let mut compiler = Compiler::new();
    let mut dry_run = options.dry_run;
    let stdin = io::stdin();
    let mut reader = stdin.lock();
    let mut line = String::new();
    loop {
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
            break;
        }
        let input = line.trim();
        if input.is_empty() {
            continue;
        }
        if matches!(input, "/quit" | "/exit" | ":quit" | ":q") {
            break;
        }
        let result = if is_repl_command(input) {
            repl_command(input, &mut compiler, &mut dry_run, options)
        } else {
            submit(
                &line,
                "<repl>",
                &mut compiler,
                "run",
                dry_run,
                options.color,
            )
        };
        if let Err(message) = result {
            print_error(&message);
        }
    }
    Ok(())
}

fn is_repl_command(input: &str) -> bool {
    // Absolute POSIX paths remain valid DSL operands.
    (input.starts_with('/') || input.starts_with(':'))
        && !input.contains("->")
        && !input.contains("=>")
        && !input.contains(":=")
}

fn repl_command(
    input: &str,
    compiler: &mut Compiler,
    dry_run: &mut bool,
    options: &Args,
) -> Result<(), String> {
    let mut trace = msync::telemetry::span("repl_command");
    let result = repl_command_inner(input, compiler, dry_run, options);
    trace.finish_result(&result);
    result
}

fn repl_command_inner(
    input: &str,
    compiler: &mut Compiler,
    dry_run: &mut bool,
    options: &Args,
) -> Result<(), String> {
    let (command, argument) = input.split_once(char::is_whitespace).unwrap_or((input, ""));
    let argument = argument.trim().trim_matches('"');
    match command.trim_start_matches(['/', ':']) {
        "help" | "?" => print_text(REPL_HELP),
        "bindings" => print_text(&format!("{}\n", compiler.bindings().join("\n"))),
        "reset" => {
            *compiler = Compiler::new();
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
                "on" => *dry_run = true,
                "off" => *dry_run = false,
                "" => {}
                _ => return Err("usage: /dry-run [on|off]".into()),
            }
            print_text(if *dry_run {
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
                "load" => submit(&source, argument, compiler, "run", *dry_run, options.color),
                mode => submit(
                    &source,
                    argument,
                    &mut compiler.clone(),
                    if mode == "plan" { "compile" } else { "check" },
                    *dry_run,
                    options.color,
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
