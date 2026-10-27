use std::error::Error;
use std::fmt;
use std::path::PathBuf;

use crate::syntax::{Report, Span};
use crate::transaction::filesystem::FsError;
use crate::transaction::manager::ManagerError;

/// A source-site failure with the native cause and any unsuccessful undo work.
#[derive(Debug)]
pub struct ExecutionError {
    /// Runtime publication cohort responsible for an asynchronous failure.
    pub transaction: Option<crate::hir::TransactionId>,
    /// Owned operation or operand source, retained independently of cohort identity.
    pub origin: Option<Box<crate::source::InstructionOrigin>>,
    pub report: Box<Report>,
    pub cause: Option<Box<dyn Error + Send + Sync>>,
    pub recovery: Vec<ManagerError>,
    pub compensation: Vec<FsError>,
    pub retained_paths: Vec<PathBuf>,
}

impl ExecutionError {
    pub(super) fn new(message: impl Into<String>, span: Span) -> Self {
        Self {
            transaction: None,
            origin: None,
            report: Box::new(Report::new(message, span)),
            cause: None,
            recovery: Vec::new(),
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
        if let Some(cause) = &self.cause {
            write!(f, ": {cause}")?;
        }
        for error in &self.recovery {
            write!(f, "; recovery: {error}")?;
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
