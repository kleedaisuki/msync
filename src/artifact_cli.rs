//! Explicit artifact commands; ordinary script command behavior stays unchanged.
use super::*;
use msync::ir::{Artifact, Contextual};

pub(super) fn validate_options(options: &Args) -> Result<(), String> {
    let exporting = matches!(options.command.as_str(), "emit-ir" | "bind-ir");
    if options.output.is_some() && !exporting {
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
    let text = read_source(file)?;
    let artifact = if options.command == "emit-ir" {
        let input = SourceUnit::file(&text, file)?;
        let script = syntax::parse_rich(&text)
            .map_err(|errors| diagnostics(file, &text, &errors, options.color))?;
        if options.ir_stage.as_deref() == Some("contextual") {
            let program = Compiler::new()
                .compile_rich_with_context(&script, &input.context)
                .map_err(|errors| diagnostics(file, &text, &errors, options.color))?;
            Artifact::Contextual(Contextual::capture(&program, &input.context).map_err(reports)?)
        } else {
            Artifact::Relocatable(
                Compiler::new()
                    .compile_relocatable(&script)
                    .map_err(|errors| diagnostics(file, &text, &errors, options.color))?,
            )
        }
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
            return print_plan(&plan, options.color);
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
