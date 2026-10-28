//! Bounded private literal IO; logical staging remains with the transaction owner.

use std::path::PathBuf;
use std::sync::Arc;

use tokio::io::AsyncWriteExt;
use tokio_util::sync::CancellationToken;

use crate::hir::{InstructionId, TransactionId};
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

#[derive(Clone)]
pub(super) enum Kind {
    Literal,
}

pub(super) struct Literal {
    pub path: PathBuf,
    pub text: Arc<str>,
    pub buffer: usize,
    pub cancel: CancellationToken,
    pub span: Span,
    pub instruction: InstructionId,
    pub transaction: TransactionId,
}

impl Literal {
    pub async fn run(self) -> Result<PathBuf> {
        if self.cancel.is_cancelled() {
            return Err(self.cancelled());
        }
        let mut file = tokio::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&self.path)
            .await
            .map_err(|cause| self.failure("cannot create private literal", cause))?;
        file.set_max_buf_size(self.buffer);
        self.event("write-start");
        for chunk in self.text.as_bytes().chunks(self.buffer) {
            if self.cancel.is_cancelled() {
                break;
            }
            file.write_all(chunk)
                .await
                .map_err(|cause| self.failure("cannot write private literal", cause))?;
        }
        // A Tokio file may retain an issued blocking write after poll_write
        // returns. Join that IO before reporting owner cancellation or readiness.
        file.flush()
            .await
            .map_err(|cause| self.failure("cannot finish private literal", cause))?;
        if self.cancel.is_cancelled() {
            return Err(self.cancelled());
        }
        self.event("write-complete");
        Ok(self.path)
    }

    fn cancelled(&self) -> ExecutionError {
        ExecutionError::cancelled("literal materialization was cancelled", self.span)
    }

    fn failure(&self, action: &str, cause: std::io::Error) -> ExecutionError {
        ExecutionError::caused(
            format!("{action} {}", self.path.display()),
            self.span,
            cause,
        )
    }

    fn event(&self, phase: &str) {
        if let Some(context) = crate::telemetry::context() {
            let path = self.path.display().to_string();
            context.event(
                "literal.capture",
                &[("phase", phase), ("capture.path", &path)],
                &[
                    ("instruction.id", self.instruction.0 as u64),
                    ("transaction.id", self.transaction.0 as u64),
                    ("bytes.total", self.text.len() as u64),
                ],
            );
        }
    }
}
