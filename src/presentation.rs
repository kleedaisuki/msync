use std::fmt;
use std::io;
use std::path::{Path, PathBuf};

use super::Color;

pub(super) type CliResult<T = ()> = Result<T, CliError>;

#[derive(Debug)]
pub(super) enum CliError {
    Message(String),
    Presented(String),
    Cancelled(Box<CliError>),
    Issue(Issue),
    Io {
        action: IoAction,
        path: Option<PathBuf>,
        cwd: Option<PathBuf>,
        directory: bool,
        cause: io::Error,
    },
}

#[derive(Clone, Copy, Debug)]
pub(super) enum IoAction {
    ReadScript,
    ReadInput,
    WriteScript,
    ChangeDirectory,
    WorkingDirectory,
}

#[derive(Debug)]
pub(super) struct Issue {
    stage: Option<Stage>,
    title: String,
    fields: Vec<(&'static str, String)>,
    hints: Vec<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Stage {
    Compile,
    Load,
    Evaluate,
    Execute,
    Cleanup,
}

impl Stage {
    pub(super) fn heading(self) -> &'static str {
        match self {
            Self::Compile => "compile error:",
            Self::Load => "load error:",
            Self::Evaluate => "evaluation error:",
            Self::Execute => "execution error:",
            Self::Cleanup => "cleanup error:",
        }
    }
}

impl From<msync::execution::DiagnosticPhase> for Stage {
    fn from(phase: msync::execution::DiagnosticPhase) -> Self {
        match phase {
            msync::execution::DiagnosticPhase::Evaluate => Self::Evaluate,
            msync::execution::DiagnosticPhase::Execute => Self::Execute,
            msync::execution::DiagnosticPhase::Cleanup => Self::Cleanup,
        }
    }
}

impl Issue {
    pub(super) fn new(title: impl Into<String>) -> Self {
        Self {
            stage: None,
            title: title.into(),
            fields: Vec::new(),
            hints: Vec::new(),
        }
    }

    pub(super) fn stage(mut self, stage: Stage) -> Self {
        self.stage = Some(stage);
        self
    }

    pub(super) fn field(mut self, name: &'static str, value: impl Into<String>) -> Self {
        self.fields.push((name, value.into()));
        self
    }

    pub(super) fn help(mut self, hint: impl Into<String>) -> Self {
        self.hints.push(hint.into());
        self
    }

    fn render(&self, color: bool) -> String {
        let heading = self.stage.map_or("error:", Stage::heading);
        let mut output = format!("{} {}", paint(heading, "1;31", color), escape(&self.title));
        for (name, value) in &self.fields {
            output.push_str(&format!(
                "\n  {} {}",
                paint(&format!("{name}:"), "1;34", color),
                escape(value)
            ));
        }
        for hint in &self.hints {
            output.push_str(&format!(
                "\n  {} {}",
                paint("help:", "1;36", color),
                escape(hint)
            ));
        }
        output
    }
}

impl CliError {
    pub(super) fn is_empty(&self) -> bool {
        if let Self::Cancelled(error) = self {
            return error.is_empty();
        }
        matches!(self, Self::Message(text) | Self::Presented(text) if text.is_empty())
    }

    pub(super) fn cancelled(self) -> Self {
        Self::Cancelled(Box::new(self))
    }

    pub(super) fn is_cancelled(&self) -> bool {
        matches!(self, Self::Cancelled(_))
    }

    pub(super) fn io(action: IoAction, path: Option<&Path>, cause: io::Error) -> Self {
        let directory = matches!(action, IoAction::ReadScript)
            && (cause.kind() == io::ErrorKind::IsADirectory
                || cause.kind() == io::ErrorKind::PermissionDenied
                    && path.is_some_and(Path::is_dir));
        let cwd = path
            .filter(|path| path.is_relative())
            .and_then(|_| std::env::current_dir().ok());
        Self::Io {
            action,
            path: path.map(Path::to_owned),
            cwd,
            directory,
            cause,
        }
    }

    pub(super) fn argument(message: String) -> Self {
        Issue::new("invalid command")
            .field("cause", message)
            .help("Run msync --help for commands, options and examples.")
            .into()
    }

    pub(super) fn render(&self, color: Color) -> String {
        let enabled = color.stderr();
        match self {
            Self::Message(message) => Issue::new(message).render(enabled),
            Self::Presented(text) => text.clone(),
            Self::Cancelled(error) => error.render(color),
            Self::Issue(issue) => issue.render(enabled),
            Self::Io {
                action,
                path,
                cwd,
                directory,
                cause,
            } => io_issue(*action, path.as_deref(), cwd.as_deref(), *directory, cause)
                .render(enabled),
        }
    }
}

fn io_issue(
    action: IoAction,
    path: Option<&Path>,
    cwd: Option<&Path>,
    directory: bool,
    cause: &io::Error,
) -> Issue {
    let title = if directory {
        "expected a script file, found a directory"
    } else {
        match (action, cause.kind()) {
            (IoAction::ReadScript, io::ErrorKind::NotFound) => "script file not found",
            (IoAction::ReadScript, _) => "cannot read script",
            (IoAction::ReadInput, _) => "cannot read standard input",
            (IoAction::WriteScript, _) => "cannot write formatted script",
            (IoAction::ChangeDirectory, _) => "cannot change working directory",
            (IoAction::WorkingDirectory, _) => "cannot determine working directory",
        }
    };
    let mut issue = Issue::new(title);
    if let Some(path) = path {
        issue = issue.field("path", path.to_string_lossy());
    }
    if let Some(cwd) = cwd {
        issue = issue.field("cwd", cwd.to_string_lossy());
    }
    issue = issue.field("cause", cause.to_string());
    let hint = if matches!(action, IoAction::WorkingDirectory) {
        "Choose an existing accessible directory, or use /cd PATH in the REPL."
    } else if directory {
        "Choose a .msync script inside this directory, not the directory itself."
    } else {
        match cause.kind() {
            io::ErrorKind::NotFound => {
                "Check the filename and parent directory. Use an absolute path, or /pwd in the REPL to inspect the working directory."
            }
            io::ErrorKind::PermissionDenied => {
                "Check permissions, read-only attributes and whether another process has locked this path."
            }
            io::ErrorKind::InvalidData => {
                "Scripts must be UTF-8 text. Choose a text script or save this file as UTF-8."
            }
            io::ErrorKind::NotADirectory => {
                "A path component is a file, not a directory. Correct the parent path."
            }
            io::ErrorKind::InvalidFilename => {
                "Use a valid filesystem path. Check reserved names and unsupported characters in each path component."
            }
            io::ErrorKind::StorageFull => {
                "Free space on this volume or choose a destination on another volume."
            }
            _ => match action {
                IoAction::ReadScript | IoAction::ReadInput => {
                    "Check that the input is a readable UTF-8 script; the original operating-system cause is shown above."
                }
                IoAction::WriteScript => {
                    "Use fmt FILE --stdout for a read-only preview, or choose a writable script file."
                }
                IoAction::ChangeDirectory | IoAction::WorkingDirectory => {
                    "Choose an existing accessible directory; /pwd shows the REPL working directory."
                }
            },
        }
    };
    issue.help(hint)
}

impl From<String> for CliError {
    fn from(message: String) -> Self {
        Self::Message(message)
    }
}

impl From<&str> for CliError {
    fn from(message: &str) -> Self {
        Self::Message(message.into())
    }
}

impl From<Issue> for CliError {
    fn from(issue: Issue) -> Self {
        Self::Issue(issue)
    }
}

impl fmt::Display for CliError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.render(Color::Never))
    }
}

impl std::error::Error for CliError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Cancelled(error) => Some(error.as_ref()),
            Self::Io { cause, .. } => Some(cause),
            _ => None,
        }
    }
}

pub(super) fn escape(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    for character in text.chars() {
        if character.is_control()
            || matches!(character, '\u{200e}' | '\u{200f}' | '\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}')
        {
            output.extend(character.escape_default());
        } else {
            output.push(character);
        }
    }
    output
}

pub(super) fn paint(text: &str, style: &str, enabled: bool) -> String {
    if enabled {
        format!("\x1b[{style}m{text}\x1b[0m")
    } else {
        text.to_owned()
    }
}

pub(super) fn human(text: &str, enabled: bool) -> String {
    let safe = escape(text);
    let mut pieces = safe.splitn(2, ':');
    let head = pieces.next().unwrap_or_default();
    match pieces.next() {
        Some(tail) => format!("{}:{tail}", paint(head, "1;36", enabled)),
        None => safe,
    }
}

pub(super) fn legacy_plan(plan: &msync::plan::Plan, color: bool) -> String {
    let mut text = format!(
        "{} {} operation(s), {} eliminated, {} forwarded, {} dependency edge(s)\n",
        paint("plan:", "1;36", color),
        plan.nodes.len(),
        plan.removed.len(),
        plan.forwarded.len(),
        plan.edges.len()
    );
    text.push_str("  Metadata preview only; no file data has been copied.\n");
    if plan.nodes.is_empty() {
        text.push_str("\n  No transfers to execute. Add a source -> destination statement.\n");
    }
    for node in &plan.nodes {
        let verb = if node.operation.pattern.is_some() {
            "select & copy"
        } else if node.kinds.source == msync::plan::FileClass::Stream
            || node.kinds.destination == msync::plan::FileClass::Stream
        {
            "flow"
        } else {
            "copy"
        };
        text.push_str(&format!(
            "\n  {} {verb}  [{} -> {}]\n",
            paint(&format!("#{}", node.id), "1;34", color),
            kind(node.kinds.source),
            kind(node.kinds.destination)
        ));
        text.push_str(&format!(
            "    {} {}\n",
            paint("from:", "1;34", color),
            location(&node.operation.source, color)
        ));
        text.push_str(&format!(
            "    {} {}\n",
            paint("  to:", "1;34", color),
            location(&node.operation.destination, color)
        ));
        if let Some(pattern) = &node.operation.pattern {
            text.push_str(&format!("    select: {}\n", escape(pattern)));
        }
        let needs = if node.dependencies.is_empty() {
            "ready".to_owned()
        } else {
            node.dependencies
                .iter()
                .map(|id| format!("#{id}"))
                .collect::<Vec<_>>()
                .join(", ")
        };
        text.push_str(&format!("    needs: {needs}\n"));
    }
    for group in &plan.stream_groups {
        let ids = group
            .iter()
            .map(|id| format!("#{id}"))
            .collect::<Vec<_>>()
            .join(", ");
        text.push_str(&format!("\n  start together: {ids}\n"));
    }
    for item in &plan.removed {
        text.push_str(&format!(
            "\n  removed input #{}: overwritten by input #{} before any read\n",
            item.original_index, item.overwritten_by
        ));
    }
    for item in &plan.forwarded {
        text.push_str(&format!(
            "\n  forwarded input #{}: {} -> {}\n",
            item.original_index,
            location(&item.from, color),
            location(&item.to, color)
        ));
    }
    for issue in &plan.issues {
        text.push_str(&format!("\n  issue: {}\n", escape(&issue.message)));
    }
    text
}

fn location(location: &msync::compiler::Location, color: bool) -> String {
    format!(
        "{} {}",
        paint(&escape(&location.backend.to_string()), "35", color),
        escape(&location.path)
    )
}

fn kind(kind: msync::plan::FileClass) -> &'static str {
    use msync::plan::FileClass;
    match kind {
        FileClass::Regular => "file",
        FileClass::Directory => "directory",
        FileClass::Stream => "pipe/socket",
        FileClass::Missing => "missing",
        FileClass::Unknown => "not inspected",
    }
}

pub(super) fn limits(
    limits: &msync::scheduler::ResourceLimits,
    spool_limit: Option<u64>,
    color: bool,
) -> String {
    format!(
        "{}\n  finite copies: {}\n  external programs: {} (separate slots)\n  background flows: {}\n  buffer size: {} ({} bytes)\n  payload budget: {} ({} bytes; not process RSS)\n  output spool: {} per program (stdout + stderr)\n",
        paint("limits:", "1;36", color),
        limits.jobs,
        limits.jobs,
        limits.max_streams,
        bytes(limits.buffer_bytes),
        limits.buffer_bytes,
        bytes(limits.memory_bytes),
        limits.memory_bytes,
        spool_limit.map_or_else(|| "unlimited".into(), |bytes| format!("{bytes} bytes"))
    )
}

fn bytes(bytes: usize) -> String {
    for (unit, size) in [
        ("GiB", 1024usize.pow(3)),
        ("MiB", 1024usize.pow(2)),
        ("KiB", 1024),
    ] {
        if bytes >= size && bytes.is_multiple_of(size) {
            return format!("{} {unit}", bytes / size);
        }
    }
    format!("{bytes} B")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn human_plan_has_an_explicit_empty_state_and_retains_a_plain_header() {
        let plan = msync::plan::Plan::default();
        let text = super::legacy_plan(&plan, false);
        assert!(text.starts_with("plan: 0 operation(s)"));
        assert!(text.contains("No transfers to execute"));
        assert!(!text.contains('\x1b'));
    }

    #[test]
    fn resource_limits_preserve_plain_and_colored_presentations() {
        let limits = msync::scheduler::ResourceLimits::default();
        let plain = super::limits(&limits, None, false);
        assert!(plain.starts_with("limits:"));
        assert!(plain.contains("finite copies:"));
        assert!(plain.contains("payload budget:"));
        assert!(!plain.contains('\x1b'));
        let colored = super::limits(&limits, Some(1024), true);
        assert!(colored.starts_with("\x1b[1;36mlimits:\x1b[0m"));
        assert!(colored.contains("not process RSS"));
    }

    #[test]
    fn io_errors_retain_paths_causes_and_actionable_help() {
        let error = CliError::io(
            IoAction::ReadScript,
            Some(Path::new("missing script.msync")),
            io::Error::from(io::ErrorKind::NotFound),
        );
        let output = error.render(Color::Never);
        for expected in [
            "script file not found",
            "path:",
            "missing script.msync",
            "cwd:",
            "cause:",
            "help:",
        ] {
            assert!(output.contains(expected), "{output}");
        }
        assert!(!output.contains('\x1b'));
    }

    #[test]
    fn runtime_context_errors_do_not_masquerade_as_compiler_errors() {
        let output = CliError::from(
            Issue::new("cannot resolve the current block")
                .stage(Stage::Evaluate)
                .field("path", "missing/input"),
        )
        .render(Color::Never);
        assert!(output.starts_with("evaluation error:"), "{output}");
        assert!(!output.contains("compile error:"));
        assert!(!output.contains(" --> "));
    }

    #[test]
    fn untrusted_fields_cannot_inject_terminal_controls() {
        let error: CliError = Issue::new("bad path").field("path", "Klee\n\x1b[2J").into();
        let output = error.render(Color::Never);
        assert!(output.contains("Klee\\n\\u{1b}[2J"));
        assert!(!output.contains('\x1b'));
    }
}
