use super::*;

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

    fn join(mut self, stop: bool, color: Color) -> CliResult {
        let stop = stop && !self.finished();
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
        if stop && let Err(report) = &result {
            print_status(&format!("job {}: stopped", self.id), color)?;
            return print_text(&format!(
                "  reason: {}\n",
                presentation::escape(&report.diagnostic.message)
            ));
        }
        let summary =
            result.map_err(|report| diagnostics(&self.file, &self.source, &[report], color))?;
        print_status(&format!("job {}: completed", self.id), color)?;
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
    fn join(&mut self, stop: bool, color: Color) -> CliResult {
        match self.active.take() {
            Some(job) => job.join(stop, color),
            None => print_status(
                "jobs: no active jobs; enter a transfer or /load FILE",
                color,
            ),
        }
    }

    fn reap(&mut self, color: Color) {
        if self.active.as_ref().is_some_and(ActiveJob::finished)
            && let Err(message) = self.join(false, color)
        {
            print_error(&message, color);
        }
    }
}

fn repl_submit(input: SourceUnit<'_>, state: &mut ReplState, options: &Args) -> CliResult {
    let mut trace = msync::telemetry::span("submission");
    input.annotate(&mut trace);
    let result = repl_submit_inner(input, state, options);
    trace.finish_result(&result);
    result
}

fn repl_submit_inner(input: SourceUnit<'_>, state: &mut ReplState, options: &Args) -> CliResult {
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
        print_plan(&plan, options.color)?;
        if copies {
            print_status(
                &format!(
                    "preview: {} operation(s); no files written\n",
                    plan.nodes.len()
                ),
                options.color,
            )?;
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
    print_status(
        &format!("job {id}: running in background; use /jobs, /wait or /stop\n"),
        options.color,
    )
}

pub(super) fn run(options: &Args) -> CliResult {
    let mut trace = msync::telemetry::span("repl");
    let result = repl_inner(options);
    trace.finish_result(&result);
    result
}

fn repl_inner(options: &Args) -> CliResult {
    let mut input = editor::Input::new(options.color.stdout()).map_err(|cause| Issue::new("cannot initialize the REPL editor")
        .field("cause", cause)
        .help("Use a supported terminal for editable input, TERM=dumb for basic input, or pass a UTF-8 script to msync run."))?;
    let interactive = input.interactive();
    if interactive {
        let features = if input.editing() {
            "/help for commands; Tab to complete; /quit to exit"
        } else {
            "basic terminal mode; /help for commands; /quit to exit"
        };
        print_status(
            &format!("msync {} — {features}", env!("CARGO_PKG_VERSION")),
            options.color,
        )?;
    }
    let mut state = ReplState {
        compiler: Compiler::new(),
        dry_run: options.dry_run,
        active: None,
        next_job: 1,
    };
    loop {
        state.reap(options.color);
        let event =
            input.read("msync> ").map_err(|cause| {
                Issue::new("cannot read REPL input")
            .field("cause", cause)
            .help("Check that the terminal is still attached and that piped input is UTF-8 text.")
            })?;
        state.reap(options.color);
        let line = match event {
            editor::InputEvent::Line(line) => line,
            editor::InputEvent::Interrupted => {
                if state.active.is_some() {
                    state.join(true, options.color)?;
                } else if interactive {
                    print_status(
                        "input: cancelled; enter another statement or /quit",
                        options.color,
                    )?;
                }
                continue;
            }
            editor::InputEvent::Eof => return finish_repl(&mut state, interactive, options.color),
        };
        state.reap(options.color);
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        if matches!(trimmed, "/quit" | "/exit" | ":quit" | ":q") {
            return finish_repl(&mut state, true, options.color);
        }
        let command_line = line.trim_start().trim_end_matches(['\r', '\n']);
        let result = if is_repl_command(command_line) {
            repl_command(command_line, &mut state, options, &mut input)
        } else {
            SourceUnit::repl(&line).and_then(|input| repl_submit(input, &mut state, options))
        };
        if let Err(message) = result {
            print_error(&message, options.color);
        }
    }
}

fn finish_repl(state: &mut ReplState, stop: bool, color: Color) -> CliResult {
    if state.active.is_some() {
        state.join(stop, color)
    } else {
        Ok(())
    }
}
fn is_repl_command(input: &str) -> bool {
    let (command, argument) = input.split_once(char::is_whitespace).unwrap_or((input, ""));
    if ["->", "=>", ":="]
        .iter()
        .any(|operator| argument.trim_start().starts_with(operator))
    {
        return false;
    }
    if command.starts_with(['/', ':'])
        && msync::highlight::COMMANDS
            .iter()
            .any(|known| known[1..] == *command.trim_start_matches(['/', ':']))
    {
        return true;
    }
    // Absolute POSIX paths remain valid DSL operands.
    (input.starts_with('/') || input.starts_with(':'))
        && !input.contains("->")
        && !input.contains("=>")
        && !input.contains(":=")
}

fn repl_command(
    input: &str,
    state: &mut ReplState,
    options: &Args,
    editor: &mut editor::Input,
) -> CliResult {
    let mut trace = msync::telemetry::span("repl_command");
    let result = repl_command_inner(input, state, options, editor);
    trace.finish_result(&result);
    result
}

fn repl_command_inner(
    input: &str,
    state: &mut ReplState,
    options: &Args,
    editor: &mut editor::Input,
) -> CliResult {
    let (command, raw_argument) = input.split_once(char::is_whitespace).unwrap_or((input, ""));
    let name = command.trim_start_matches(['/', ':']);
    let argument = if matches!(name, "bash" | "pwsh") {
        raw_argument
    } else {
        path_argument(raw_argument)?
    };
    if matches!(
        name,
        "help"
            | "?"
            | "jobs"
            | "wait"
            | "stop"
            | "reset"
            | "pwd"
            | "bindings"
            | "limits"
            | "clear"
            | "quit"
            | "exit"
    ) && !raw_argument.trim().is_empty()
    {
        return Err(Issue::new(format!("{command} does not accept arguments"))
            .help(format!("Usage: {command}"))
            .into());
    }
    if state.active.is_some() && matches!(name, "cd" | "fmt" | "bash" | "pwsh") {
        return Err(format!(
            "{command} changes shared working-directory or filesystem state; use /wait or /stop before this command"
        ).into());
    }
    match name {
        "clear" => {
            if !editor.clear().map_err(|cause| Issue::new("cannot clear the terminal viewport")
                .field("cause", cause)
                .help("Bindings, history and jobs are unchanged. Reconnect a supported terminal or continue entering commands."))? {
                print_text(
                    "clear: terminal screen clearing is unavailable here; bindings and history are unchanged\n",
                )?;
            }
            Ok(())
        }
        "bash" | "pwsh" => run_shell(name, raw_argument, editor.interactive(), options.color),
        "jobs" => match &state.active {
            Some(job) => print_status(&format!("job {}: running", job.id), options.color),
            None => print_status(
                "jobs: no active jobs; enter a transfer or /load FILE",
                options.color,
            ),
        },
        "wait" => state.join(false, options.color),
        "stop" => state.join(true, options.color),
        "help" | "?" => print_help(REPL_HELP, options.color),
        "limits" => print_limits(&options.limits, options.color),
        "bindings" => {
            let bindings = state.compiler.bindings();
            let text = if bindings.is_empty() {
                "# no user bindings; try: root := $pwd::pwd\n".to_owned()
            } else {
                format!("{}\n", bindings.join("\n"))
            };
            print_text(&msync::highlight::highlight(&text, options.color.stdout()))
        }
        "reset" => {
            state.compiler = Compiler::new();
            print_status(
                "bindings cleared: ready for new declarations",
                options.color,
            )
        }
        "pwd" => print_text(&format!(
            "{}\n",
            presentation::escape(
                &env::current_dir()
                    .map_err(|error| CliError::io(IoAction::WorkingDirectory, None, error))?
                    .to_string_lossy()
            )
        )),
        "cd" => {
            if argument.is_empty() {
                return Err("usage: /cd PATH".into());
            }
            env::set_current_dir(argument).map_err(|error| {
                CliError::io(IoAction::ChangeDirectory, Some(Path::new(argument)), error)
            })
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
                return Err(format!("usage: {command} FILE (not stdin)").into());
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
        _ => {
            let mut issue = Issue::new(format!("unknown REPL command '{command}'"));
            if let Some(candidate) =
                msync::suggest::closest(command, msync::highlight::COMMANDS.iter().copied())
            {
                issue = issue.help(format!("Did you mean {candidate}?"));
            }
            Err(issue.help("Use /help or /? to see commands. Enter DSL paths with an explicit -> transfer operator.").into())
        }
    }
}

fn run_shell(name: &str, argument: &str, interactive: bool, color: Color) -> CliResult {
    if argument.trim().is_empty() && interactive {
        let location = if name == "bash" && cfg!(windows) {
            " (WSL)"
        } else {
            ""
        };
        print_status(
            &format!("shell: /{name}{location}; type exit to return to msync"),
            color,
        )?;
    }
    let status = shell::launch(name, argument, interactive)?;
    if !status.success() {
        let detail = status
            .code()
            .map_or_else(|| status.to_string(), |code| format!("exit status {code}"));
        return Err(Issue::new(format!("/{name} finished unsuccessfully"))
            .field("status", detail)
            .help("The REPL is still available; its working directory and bindings are unchanged.")
            .into());
    }
    if interactive {
        print_status(
            &format!("shell: returned from /{name}; working directory and bindings are unchanged"),
            color,
        )?;
    }
    Ok(())
}

fn path_argument(argument: &str) -> CliResult<&str> {
    let argument = argument.trim();
    if let Some(quoted) = argument.strip_prefix('"') {
        return quoted.strip_suffix('"').ok_or_else(|| {
            Issue::new("unclosed quoted command argument")
                .help(
                    "Close the path with a double quote, or pass an unquoted path without spaces.",
                )
                .into()
        });
    }
    Ok(argument)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_shortcuts_keep_shell_operators_out_of_the_dsl_dispatcher() {
        assert!(is_repl_command("/bash printf '%s' 'a -> b := c'"));
        assert!(is_repl_command("/pwsh Write-Output 'x => y'"));
        assert!(!is_repl_command("/tmp/source -> /tmp/destination"));
        assert!(!is_repl_command("/help -> output"));
        assert!(!is_repl_command("/reset -> output"));
        assert!(!is_repl_command("/bash => value"));
        assert!(is_repl_command("/cd \"folder->name\""));
    }
}
