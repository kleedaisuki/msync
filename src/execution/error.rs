use std::error::Error;
use std::fmt;
use std::path::PathBuf;

use crate::filesystem::FsError;
use crate::syntax::{Report, Span};

#[derive(Debug)]
struct OwnerCancellation;

impl fmt::Display for OwnerCancellation {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        output.write_str("requested by the execution owner")
    }
}

impl Error for OwnerCancellation {}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiagnosticPhase {
    Evaluate,
    Execute,
    Cleanup,
}

impl DiagnosticPhase {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Evaluate => "evaluate",
            Self::Execute => "execute",
            Self::Cleanup => "cleanup",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiagnosticCategory {
    Operation,
    Capability,
    Cleanup,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiagnosticLocation {
    None,
    Primary,
    Context,
}

/// A source location can explain why an operation needs a capability without
/// claiming that the source token itself is invalid.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DiagnosticContext {
    pub phase: DiagnosticPhase,
    pub category: DiagnosticCategory,
    pub location: DiagnosticLocation,
    pub action: &'static str,
    pub backend: Option<&'static str>,
    pub instruction: Option<crate::hir::InstructionId>,
}

#[derive(Debug)]
struct ContextualCause {
    context: DiagnosticContext,
    cause: Option<Box<dyn Error + Send + Sync>>,
}

impl fmt::Display for ContextualCause {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        output.write_str(self.context.action)
    }
}

impl Error for ContextualCause {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        self.cause
            .as_deref()
            .map(|cause| cause as &(dyn Error + 'static))
    }
}

/// A source-site failure with its native cause and statement compensation errors.
#[derive(Debug)]
pub struct ExecutionError {
    /// Owned operation or operand source, retained independently of runtime tasks.
    pub origin: Option<Box<crate::source::InstructionOrigin>>,
    pub report: Box<Report>,
    pub cause: Option<Box<dyn Error + Send + Sync>>,
    pub compensation: Vec<FsError>,
    pub retained_paths: Vec<PathBuf>,
}

impl ExecutionError {
    /// Describe the failed stage without interpreting the diagnostic's wording.
    /// An empty fallback span does not identify the first source token.
    pub fn context(&self) -> DiagnosticContext {
        if let Some(cause) = self
            .cause
            .as_deref()
            .and_then(|cause| cause.downcast_ref::<ContextualCause>())
        {
            return cause.context;
        }
        let span = self.report.diagnostic.span;
        let has_source = span.end > span.start
            || self
                .origin
                .as_ref()
                .is_some_and(|origin| origin.site.span.end > origin.site.span.start);
        DiagnosticContext {
            phase: DiagnosticPhase::Execute,
            category: DiagnosticCategory::Operation,
            location: if has_source {
                DiagnosticLocation::Primary
            } else {
                DiagnosticLocation::None
            },
            action: "execute operation",
            backend: None,
            instruction: None,
        }
    }

    /// Access the original native cause independently of diagnostic metadata.
    /// Callers may also follow the standard `Error::source` chain.
    pub fn native_cause(&self) -> Option<&(dyn Error + Send + Sync + 'static)> {
        let cause = self.cause.as_deref()?;
        match cause.downcast_ref::<ContextualCause>() {
            Some(context) => context.cause.as_deref(),
            None => Some(cause),
        }
    }

    pub(super) fn with_context(mut self, context: DiagnosticContext) -> Self {
        let cause = match self.cause.take() {
            Some(cause) if cause.is::<ContextualCause>() => {
                cause
                    .downcast::<ContextualCause>()
                    .expect("context type was checked")
                    .cause
            }
            cause => cause,
        };
        self.cause = Some(Box::new(ContextualCause { context, cause }));
        self
    }

    pub(super) fn new(message: impl Into<String>, span: Span) -> Self {
        Self {
            origin: None,
            report: Box::new(Report::new(message, span)),
            cause: None,
            compensation: Vec::new(),
            retained_paths: Vec::new(),
        }
    }

    pub(super) fn caused(
        message: impl Into<String>,
        span: Span,
        cause: impl Error + Send + Sync + 'static,
    ) -> Self {
        let mut failure = Self::new(message, span);
        failure.cause = Some(Box::new(cause));
        failure
    }

    pub(super) fn cancelled(message: impl Into<String>, span: Span) -> Self {
        Self::caused(message, span, OwnerCancellation)
    }

    pub fn is_cancelled(&self) -> bool {
        self.compensation.is_empty()
            && self.retained_paths.is_empty()
            && self
                .native_cause()
                .is_some_and(|cause| cause.is::<OwnerCancellation>())
    }

    pub(super) fn with_origin(mut self, origin: Option<&crate::source::InstructionOrigin>) -> Self {
        if self.origin.is_none() {
            self.origin = origin.cloned().map(Box::new);
        }
        self
    }

    pub(super) fn with_operand(
        mut self,
        origin: Option<&crate::source::InstructionOrigin>,
        destination: bool,
    ) -> Self {
        if self.origin.is_none() {
            self.origin = origin.map(|origin| Box::new(origin.for_operand(destination)));
        }
        self
    }

    pub(super) fn help(mut self, text: impl Into<String>) -> Self {
        self.report.help.push(text.into());
        self
    }
}

impl fmt::Display for ExecutionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.report.diagnostic.message)?;
        if let Some(cause) = self.native_cause() {
            write!(f, ": {cause}")?;
        }
        for error in &self.compensation {
            write!(f, "; compensation: {error}")?;
        }
        if !self.retained_paths.is_empty() {
            write!(f, "; retained paths: {:?}", self.retained_paths)?;
        }
        Ok(())
    }
}

impl Error for ExecutionError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        self.cause
            .as_deref()
            .map(|cause| cause as &(dyn Error + 'static))
    }
}

pub(super) type Result<T> = std::result::Result<T, ExecutionError>;

#[cfg(test)]
mod tests {
    use super::*;

    fn evaluation_context() -> DiagnosticContext {
        DiagnosticContext {
            phase: DiagnosticPhase::Evaluate,
            category: DiagnosticCategory::Capability,
            location: DiagnosticLocation::Context,
            action: "evaluate native program endpoint",
            backend: Some("test provider"),
            instruction: Some(crate::hir::InstructionId(7)),
        }
    }

    #[test]
    fn an_unlocated_runtime_failure_is_not_an_invalid_first_token() {
        let error = ExecutionError::new("runtime initialization failed", Span::default());
        assert_eq!(error.context().phase, DiagnosticPhase::Execute);
        assert_eq!(error.context().location, DiagnosticLocation::None);
        let error = ExecutionError::new("bad destination", Span { start: 4, end: 8 });
        assert_eq!(error.context().location, DiagnosticLocation::Primary);
    }

    #[test]
    fn contextual_capability_errors_keep_the_original_native_cause_chain() {
        let error = ExecutionError::caused(
            "missing provider",
            Span { start: 14, end: 20 },
            std::io::Error::from_raw_os_error(2),
        )
        .with_context(evaluation_context());
        assert_eq!(error.context(), evaluation_context());
        let native = error
            .native_cause()
            .unwrap()
            .downcast_ref::<std::io::Error>()
            .unwrap();
        assert_eq!(native.raw_os_error(), Some(2));
        assert!(
            error
                .source()
                .unwrap()
                .source()
                .unwrap()
                .is::<std::io::Error>()
        );
        let mut replacement = evaluation_context();
        replacement.phase = DiagnosticPhase::Cleanup;
        let replaced = error.with_context(replacement);
        assert_eq!(replaced.context().phase, DiagnosticPhase::Cleanup);
        assert!(replaced.native_cause().unwrap().is::<std::io::Error>());
    }

    #[test]
    fn attaching_diagnostic_phase_does_not_change_cancellation_ownership() {
        let error =
            ExecutionError::cancelled("stop", Span::default()).with_context(evaluation_context());
        assert!(error.is_cancelled());
    }

    #[test]
    fn cancellation_requires_the_owner_marker_not_error_text_or_missing_cause() {
        assert!(ExecutionError::cancelled("stop", Span::default()).is_cancelled());
        assert!(!ExecutionError::new("cancelled", Span::default()).is_cancelled());
        let native = ExecutionError::caused(
            "cancelled",
            Span::default(),
            std::io::Error::new(std::io::ErrorKind::Interrupted, "cancelled"),
        );
        assert!(!native.is_cancelled());
    }

    #[test]
    fn cancellation_does_not_hide_retained_resources() {
        let mut retained = ExecutionError::cancelled("stop", Span::default());
        retained.retained_paths.push(PathBuf::from("owned-journal"));
        assert!(!retained.is_cancelled());
    }

    #[test]
    fn cancellation_does_not_hide_failed_statement_compensation() {
        let mut failure = ExecutionError::cancelled("stop", Span::default());
        failure.compensation.push(FsError {
            action: "restore previous destination",
            path: PathBuf::from("destination"),
            cause: std::io::Error::other("destination could not be restored"),
            journal: Some(PathBuf::from("owned-journal")),
            recovery: crate::filesystem::Recovery::JournalRetained,
            recovery_error: None,
            help: None,
        });
        assert!(!failure.is_cancelled());
        assert!(failure.to_string().contains("compensation"));
    }
}
