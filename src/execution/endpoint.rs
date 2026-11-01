//! Finite remote copies use one native exec and ephemeral same-parent staging.

use std::ffi::OsString;
use std::path::PathBuf;

use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::filesystem::InvocationRoot;
use crate::hir::{Input, Output};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

#[path = "remote.rs"]
mod remote;
pub(super) use remote::execute;

#[derive(Clone, Debug)]
pub(super) struct Spec {
    pub destination: Location,
    pub mode: DestinationMode,
    pub span: Span,
    pattern: Option<String>,
    literal: bool,
    exclusion: Option<InvocationRoot>,
}

pub(super) struct Report {
    pub transfer: crate::runtime::ExecutionReport,
    pub cleanup: Option<super::CleanupIssue>,
}

pub(super) fn inspect(source: &Input, output: &Output, span: Span) -> Result<Option<Spec>> {
    let Output::File { location, mode } = output else {
        return Ok(None);
    };
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Ok(None);
    }
    let pattern = match source {
        Input::File { pattern, .. } => pattern.clone(),
        _ => None,
    };
    Ok(Some(Spec {
        destination: location.clone(),
        mode: *mode,
        span,
        pattern,
        literal: matches!(source, Input::Literal(_)),
        exclusion: None,
    }))
}

impl Spec {
    pub(super) fn with_exclusion(mut self, exclusion: Option<InvocationRoot>) -> Self {
        self.exclusion = exclusion;
        self
    }

    pub(super) async fn run_remote(
        self,
        source: Location,
        cancel: CancellationToken,
    ) -> Result<Report> {
        let span = self.span;
        let trace = crate::telemetry::context();
        crate::runtime::ssh::spawn_blocking(move || {
            crate::telemetry::with_context(trace.as_ref(), || {
                let selected = self
                    .pattern
                    .as_ref()
                    .map(|pattern| regex::Regex::new(&format!("\\A(?:{pattern})\\z")))
                    .transpose()
                    .map_err(|cause| {
                        ExecutionError::caused("invalid source selector", span, cause)
                    })?;
                let transfer = crate::runtime::ssh_channel::copy_remote(
                    crate::runtime::ssh_channel::RemoteCopy {
                        source: &source,
                        destination: &self.destination,
                        mode: self.mode,
                        selected: selected.as_ref(),
                    },
                    &cancel,
                )
                .map_err(|message| ExecutionError::new(message, span))?;
                Ok(Report {
                    transfer,
                    cleanup: None,
                })
            })
        })
        .await
        .map_err(|cause| ExecutionError::caused("remote copy worker failed", span, cause))?
    }

    pub(super) async fn run(
        self,
        source: PathBuf,
        basename: Option<OsString>,
        _: usize,
        cancel: CancellationToken,
        _: PathBuf,
    ) -> Result<Report> {
        let span = self.span;
        let trace = crate::telemetry::context();
        crate::runtime::ssh::spawn_blocking(move || {
            crate::telemetry::with_context(trace.as_ref(), || {
                let selected = self
                    .pattern
                    .as_ref()
                    .map(|pattern| regex::Regex::new(&format!("\\A(?:{pattern})\\z")))
                    .transpose()
                    .map_err(|cause| {
                        ExecutionError::caused("invalid source selector", span, cause)
                    })?;
                let transfer = crate::runtime::ssh_channel::upload(
                    crate::runtime::ssh_channel::Upload {
                        source: &source,
                        destination: &self.destination,
                        mode: self.mode,
                        basename: basename.as_deref(),
                        selected: selected.as_ref(),
                        literal: self.literal,
                        exclusion: self.exclusion.as_ref(),
                    },
                    &cancel,
                )
                .map_err(|message| ExecutionError::new(message, span))?;
                Ok(Report {
                    transfer,
                    cleanup: None,
                })
            })
        })
        .await
        .map_err(|cause| ExecutionError::caused("remote transfer worker failed", span, cause))?
    }
}
