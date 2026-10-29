use super::*;
use std::thread;

struct ReplState {
    structured: structured::State,
    syntax: Version,
    dry_run: bool,
}

impl ReplState {
    fn read(
        &mut self,
        input: &mut editor::Input,
        prompt: &str,
        color: Color,
    ) -> Result<editor::InputEvent, String> {
        if !self.structured.active() {
            return input.read(prompt);
        }
        thread::scope(|scope| {
            let (sender, receiver) = std::sync::mpsc::sync_channel(1);
            let reader = scope.spawn(move || {
                let _ = sender.send(input.read(prompt));
            });
            let result = loop {
                if !self.structured.active() {
                    break receiver
                        .recv()
                        .map_err(|_| "input reader exited without a result".to_owned());
                }
                match receiver.recv_timeout(std::time::Duration::from_millis(20)) {
                    Ok(event) => break Ok(event),
                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => self.structured.poll(color),
                    Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                        break Err("input reader exited without a result".to_owned());
                    }
                }
            };
            reader
                .join()
                .map_err(|_| "input reader panicked".to_owned())?;
            result?
        })
    }

    fn join(&mut self, stop: bool, color: Color) -> CliResult {
        self.structured.wait(stop, color)
    }

    fn reap(&mut self, color: Color) {
        if let Err(message) = self.structured.reap(color) {
            print_error(&message, color);
        }
    }
}

fn repl_submit(input: SourceUnit<'_>, state: &mut ReplState, options: &Args) -> CliResult {
    language::select_version(input.text, None, state.syntax)
        .map_err(|reports| diagnostics(input.name, input.text, &reports, options.color))?;
    state
        .structured
        .submit(input, "run", state.dry_run, options, true)
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
        structured: structured::State::default(),
        syntax: options.syntax.unwrap_or(Version::Structured),
        dry_run: options.dry_run,
    };
    let mut pending = language::SubmissionBuffer::default();
    loop {
        state.reap(options.color);
        let event = state
            .read(
                &mut input,
                if pending.is_pending() {
                    "....> "
                } else {
                    "msync> "
                },
                options.color,
            )
            .map_err(|cause| {
                Issue::new("cannot read REPL input")
            .field("cause", cause)
            .help("Check that the terminal is still attached and that piped input is UTF-8 text.")
            })?;
        state.reap(options.color);
        let line = match event {
            editor::InputEvent::Line(line) => line,
            editor::InputEvent::Interrupted => {
                pending.cancel();
                if state.structured.active() {
                    state.join(true, options.color)?;
                } else if interactive {
                    print_status(
                        "input: cancelled; enter another statement or /quit",
                        options.color,
                    )?;
                }
                continue;
            }
            editor::InputEvent::Eof => {
                if let Some(source) = pending.finish()
                    && let Err(error) = SourceUnit::repl(&source)
                        .and_then(|input| repl_submit(input, &mut state, options))
                {
                    print_error(&error, options.color);
                }
                return finish_repl(&mut state, interactive, options.color);
            }
        };
        state.reap(options.color);
        let trimmed = line.trim();
        if trimmed.is_empty() && !pending.is_pending() {
            continue;
        }
        if matches!(trimmed, "/quit" | "/exit" | ":quit" | ":q") {
            pending.cancel();
            return finish_repl(&mut state, true, options.color);
        }
        let command_line = line.trim_start().trim_end_matches(['\r', '\n']);
        let result = if is_repl_command(command_line) {
            if pending.is_pending()
                && command_line
                    .split_whitespace()
                    .next()
                    .is_some_and(|command| command.trim_start_matches(['/', ':']) == "syntax")
            {
                print_error(
                    &Issue::new("finish or cancel the current block before changing syntax").into(),
                    options.color,
                );
                continue;
            }
            repl_command(command_line, &mut state, options, &mut input)
        } else {
            let source = match pending.push_line(&line) {
                Ok(language::Submission::Ready(source)) => source,
                Ok(_) => continue,
                Err(report) => {
                    print_error(
                        &diagnostics("<repl>", &line, &[report], options.color),
                        options.color,
                    );
                    continue;
                }
            };
            SourceUnit::repl(&source).and_then(|input| repl_submit(input, &mut state, options))
        };
        if let Err(message) = result {
            print_error(&message, options.color);
        }
    }
}

fn finish_repl(state: &mut ReplState, stop: bool, color: Color) -> CliResult {
    state.structured.finish(stop, color)
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
            | "kill"
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
    if (state.structured.active())
        && matches!(name, "cd" | "fmt" | "bash" | "pwsh" | "syntax" | "reset")
    {
        return Err(format!(
            "{command} changes shared bindings, working-directory or filesystem state; use /wait or /stop before this command"
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
        "jobs" => state.structured.jobs(options.color),
        "wait" => state.join(false, options.color),
        "stop" => state.join(true, options.color),
        "kill" => state.structured.force_stop(options.color),
        "help" | "?" => print_help(REPL_HELP, options.color),
        "limits" => print_limits(&options.limits, options.spool_limit, options.color),
        "bindings" => {
            let bindings = state.structured.compiler.bindings();
            let text = if bindings.is_empty() {
                "# no user bindings; try: root := $pwd::pwd\n".to_owned()
            } else {
                format!("{}\n", bindings.join("\n"))
            };
            print_text(&msync::highlight::highlight(&text, options.color.stdout()))
        }
        "reset" => {
            state.structured.compiler.reset();
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
        "syntax" => {
            if !argument.is_empty() {
                let version = argument
                    .parse::<Version>()
                    .map_err(|help| Issue::new("unsupported syntax version").help(help))?;
                state.syntax = version;
            }
            print_status(&format!("syntax: {}", state.syntax), options.color)
        }
        "load" | "check" | "plan" | "fmt" => {
            if argument.is_empty() || argument == "-" {
                return Err(format!("usage: {command} FILE (not stdin)").into());
            }
            let input = read_input(argument)?;
            if msync::object::is_object(&input) {
                return object_command(
                    command.trim_start_matches(['/', ':']),
                    argument,
                    &input,
                    state,
                    options,
                );
            }
            let source = source_text(argument, input)?;
            match command.trim_start_matches(['/', ':']) {
                "fmt" => {
                    let mut formatting = options.clone();
                    formatting.syntax = Some(
                        language::select_version(&source, None, state.syntax).map_err(
                            |reports| diagnostics(argument, &source, &reports, options.color),
                        )?,
                    );
                    format_file(argument, &source, &formatting)
                }
                "load" => repl_submit(SourceUnit::file(&source, argument)?, state, options),
                mode => {
                    language::select_version(&source, None, state.syntax).map_err(|reports| {
                        diagnostics(argument, &source, &reports, options.color)
                    })?;
                    let input = SourceUnit::file(&source, argument)?;
                    let saved = state.structured.compiler.clone();
                    let result =
                        state
                            .structured
                            .submit(input, mode, state.dry_run, options, false);
                    state.structured.compiler = saved;
                    result
                }
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

fn object_command(
    command: &str,
    name: &str,
    bytes: &[u8],
    state: &mut ReplState,
    options: &Args,
) -> CliResult {
    if command == "fmt" {
        return Err(Issue::new("compiled objects are not source scripts")
            .stage(presentation::Stage::Load)
            .field("object", name)
            .help("Format the original .msync source, then compile a new object.")
            .into());
    }
    let input = ObjectUnit::file(bytes, name)?;
    let previous = (command != "load").then(|| state.structured.compiler.clone());
    let mut loading = options.clone();
    loading.syntax = None;
    let outcome = state.structured.submit_object(
        input,
        if command == "load" { "run" } else { command },
        state.dry_run,
        &loading,
        command == "load",
    );
    if let Some(previous) = previous {
        state.structured.compiler = previous;
    }
    outcome
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
