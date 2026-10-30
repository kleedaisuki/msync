//! Explicit artifact commands; ordinary script command behavior stays unchanged.
use super::*;
use msync::ir::{Artifact, Contextual, Structured};
use msync::{compiler::structured::Session, scheduler};

pub(super) fn validate_options(options: &Args) -> Result<(), String> {
    let exporting = matches!(options.command.as_str(), "emit-ir" | "bind-ir");
    if options.output.is_some() && !exporting && options.command != "compile" {
        return Err("--output is only valid with emit-ir or bind-ir".into());
    }
    if options.ir_stage.is_some() && options.command != "emit-ir" {
        return Err("--ir-stage is only valid with emit-ir".into());
    }
    if options
        .ir_stage
        .as_deref()
        .is_some_and(|stage| !matches!(stage, "relocatable" | "contextual"))
    {
        return Err("--ir-stage must be relocatable or contextual".into());
    }
    if options.source_file.is_some()
        && !matches!(options.command.as_str(), "bind-ir" | "plan-ir" | "run-ir")
    {
        return Err("--source-file is only valid with bind-ir, plan-ir or run-ir".into());
    }
    if exporting && options.dry_run {
        return Err(
            "IR export writes an artifact, not a transfer preview; use --output - for stdout"
                .into(),
        );
    }
    Ok(())
}

pub(super) fn export(artifact: &Artifact, output: &str) -> CliResult {
    let json = artifact.to_json()?;
    if output == "-" {
        return print_text(&json);
    }
    let path = Path::new(output);
    let stage = msync::io::StagedFile::new(path, false)
        .map_err(|error| CliError::io(IoAction::WriteScript, Some(path), error))?;
    fs::write(stage.path(), json)
        .and_then(|()| stage.preserve_target_permissions())
        .and_then(|()| stage.commit())
        .map_err(|error| CliError::io(IoAction::WriteScript, Some(path), error))
}

pub(super) fn reports(reports: Vec<Report>) -> CliError {
    Issue::new("cannot bind or validate IR")
        .field("cause", reports.into_iter().map(|report| report.diagnostic.message).collect::<Vec<_>>().join("; "))
        .help("Check the artifact paths and provide --source-file FILE if a relocatable artifact uses $file.").into()
}

pub(super) fn run(options: &Args) -> CliResult {
    let file = options
        .file
        .as_deref()
        .ok_or("missing input file; try --help")?;
    let text = if file == "-" {
        let mut text = String::new();
        io::stdin()
            .read_to_string(&mut text)
            .map_err(|error| error.to_string())?;
        text
    } else {
        fs::read_to_string(file)
            .map_err(|error| CliError::io(IoAction::ReadScript, Some(Path::new(file)), error))?
    };
    let artifact = if options.command == "emit-ir" {
        let input = SourceUnit::file(&text, file)?;
        language::select_version(&text, options.syntax, Version::Structured)
            .map_err(|errors| diagnostics(file, &text, &errors, options.color))?;
        let lowered = msync::pipeline::compile_relocatable_named(
            &mut Session::new(),
            &text,
            file,
            &input.context,
            &Default::default(),
        )
        .map_err(|errors| structured::located(errors, options.color, "frontend"))?;
        let module = lowered.into_object().map_err(|error| error.to_string())?;
        let ir = Structured::relocatable(module)?;
        let ir = if options.ir_stage.as_deref() == Some("contextual") {
            ir.bind(&input.context)?
        } else {
            ir
        };
        Artifact::Structured(Box::new(ir))
    } else {
        Artifact::from_json(&text).map_err(|cause| {
            Issue::new("cannot read IR artifact")
                .field("path", file)
                .field("cause", cause)
        })?
    };
    if options.command == "emit-ir" {
        return export(&artifact, options.output.as_deref().unwrap_or("-"));
    }
    let context = CompileContext::capture(options.source_file.as_deref().map(Path::new))
        .map_err(|error| CliError::io(IoAction::WorkingDirectory, None, error))?;
    if let Artifact::Structured(ir) = &artifact {
        if options.command == "bind-ir" {
            return export(
                &Artifact::Structured(Box::new(ir.bind(&context)?)),
                options.output.as_deref().unwrap_or("-"),
            );
        }
        let mut state = structured::State::default();
        let result = state.submit_ir(ir, &context, options, false);
        let cleanup = state.finish(result.is_err(), options.color);
        return result.and(cleanup);
    }
    let bound = artifact.bind(&context).map_err(reports)?;
    if options.command == "bind-ir" {
        return export(
            &Artifact::Contextual(bound),
            options.output.as_deref().unwrap_or("-"),
        );
    }
    execute(
        &bound,
        options,
        &msync::runtime::ssh::SshContext::default(),
        options.command == "plan-ir",
        options.dry_run,
    )
}

pub(super) fn execute(
    bound: &Contextual,
    options: &Args,
    sessions: &msync::runtime::ssh::SshContext,
    plan_only: bool,
    dry_run: bool,
) -> CliResult {
    let label = bound
        .context()
        .source_file
        .as_deref()
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_else(|| "Current msync transfer".to_owned());
    let authentication =
        auth::Submission::foreground(bound.program(), !dry_run, sessions)?.with_label(&label);
    authentication.run(|| {
        let plan = scheduler::prepare(bound.program(), !dry_run).map_err(|error| {
            Issue::new("cannot prepare IR execution").field("cause", error.message)
        })?;
        if plan_only || dry_run {
            return print_legacy_plan(&plan, options.color);
        }
        let removed = plan.removed.len();
        let summary = scheduler::execute_plan_cancellable(
            &plan,
            &options.limits,
            authentication.cancellation(),
        )
        .map_err(|error| Issue::new("IR execution failed").field("cause", error.message))?;
        print_summary(summary, removed, true, options.color)
    })
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

fn print_legacy_plan(plan: &msync::plan::Plan, color: Color) -> CliResult {
    if io::stdout().is_terminal() {
        return print_text(&presentation::legacy_plan(plan, color.stdout()));
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
