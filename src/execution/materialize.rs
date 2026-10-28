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

#[cfg(test)]
mod tests {
    use std::fs;

    use super::*;

    fn fixture() -> tempfile::TempDir {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("materialize-v4-")
            .tempdir_in(root)
            .unwrap()
    }

    fn literal(path: PathBuf) -> Literal {
        Literal {
            path,
            text: Arc::from("literal 可莉".repeat(1024)),
            buffer: 17,
            cancel: CancellationToken::new(),
            span: Span::default(),
            instruction: InstructionId(1),
            transaction: TransactionId(1),
        }
    }

    #[tokio::test]
    async fn readiness_joins_all_configured_chunks_and_closes_the_file() {
        let root = fixture();
        let path = root.path().join("literal");
        let operation = literal(path.clone());
        let expected = operation.text.clone();
        assert_eq!(operation.run().await.unwrap(), path);
        assert_eq!(fs::read(&path).unwrap(), expected.as_bytes());
        fs::remove_file(path).unwrap();
    }

    #[tokio::test]
    async fn cancellation_before_open_does_not_create_a_file() {
        let root = fixture();
        let path = root.path().join("literal");
        let operation = literal(path.clone());
        operation.cancel.cancel();
        assert!(operation.run().await.unwrap_err().is_cancelled());
        assert!(!path.exists());
    }

    #[tokio::test]
    async fn exclusive_open_retains_the_native_error_and_existing_bytes() {
        let root = fixture();
        let path = root.path().join("literal");
        fs::write(&path, b"existing").unwrap();
        let error = literal(path.clone()).run().await.unwrap_err();
        let cause = error.cause.unwrap();
        let cause = cause.downcast_ref::<std::io::Error>().unwrap();
        assert_eq!(cause.kind(), std::io::ErrorKind::AlreadyExists);
        assert!(cause.raw_os_error().is_some());
        assert_eq!(fs::read(path).unwrap(), b"existing");
    }
}
