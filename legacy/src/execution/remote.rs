//! Borrow finite local sources; only literals and remote downloads need scratch.

use std::ffi::OsString;
use std::fs;
use std::path::PathBuf;

use crate::endpoint::Backend;
use crate::filesystem::InvocationRoot;
use crate::hir::{Input, Instruction, InstructionKind};
use crate::plan::FileClass;
use crate::source::InstructionOrigin;
use crate::syntax::Span;

use super::super::error::{ExecutionError, Result};
use super::super::{CleanupIssue, ExecutionReport, Options};

pub(crate) async fn execute(
    node: &Instruction,
    options: &Options,
    origin: Option<&InstructionOrigin>,
) -> Result<Option<ExecutionReport>> {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &node.kind
    else {
        return Ok(None);
    };
    if matches!(source, Input::Port { .. }) {
        return Ok(None);
    }
    let Some(spec) = super::inspect(source, destination, node.span)? else {
        return Ok(None);
    };
    if let Input::File { location, .. } = source
        && location.backend == spec.destination.backend
    {
        let remote = spec
            .run_remote(location.clone(), options.cancel.clone())
            .await
            .map_err(|error| error.with_operand(origin, true))?;
        return Ok(Some(ExecutionReport {
            assignments: 1,
            assigned_files: remote.transfer.copied_files,
            assigned_bytes: remote.transfer.transferred_bytes,
            published_roots: 1,
            ..Default::default()
        }));
    }
    if let Input::File { location, .. } = source
        && !matches!(location.backend, Backend::Ssh { .. } | Backend::Wsl)
        && crate::runtime::inspect_location(location, true)
            .map_err(|message| ExecutionError::new(message, node.span))?
            == FileClass::Stream
    {
        return Ok(None);
    }
    let input = source.clone();
    let capture_options = options.clone();
    let span = node.span;
    let mut material = crate::runtime::ssh::spawn_blocking(move || {
        Material::capture(&input, &capture_options, span)
    })
    .await
    .map_err(|cause| ExecutionError::caused("source acquisition worker failed", span, cause))??;
    let result = spec
        .with_exclusion(options.shared_invocation.clone())
        .run(
            material.path.clone(),
            material.basename.clone(),
            options.limits.buffer_bytes,
            options.cancel.clone(),
            options.scratch_root.clone(),
        )
        .await
        .map_err(|error| error.with_operand(origin, true));
    let cleanup = material.close();
    match result {
        Ok(remote) => {
            let mut report = ExecutionReport {
                assignments: 1,
                assigned_files: remote.transfer.copied_files,
                assigned_bytes: remote.transfer.transferred_bytes,
                published_roots: 1,
                ..Default::default()
            };
            report.cleanup.extend(remote.cleanup);
            report.cleanup.extend(cleanup);
            Ok(Some(report))
        }
        Err(mut error) => {
            for issue in cleanup {
                error.retained_paths.extend(issue.retained_paths);
                error.report.help.push(format!("cleanup: {}", issue.cause));
            }
            Err(error)
        }
    }
}

struct Material {
    path: PathBuf,
    basename: Option<OsString>,
    temporary: Option<InvocationRoot>,
}

impl Material {
    fn capture(input: &Input, options: &Options, span: Span) -> Result<Self> {
        options.check(span)?;
        if let Input::File { location, .. } = input
            && let Some(path) = crate::runtime::local_path(location, true)
                .map_err(|message| ExecutionError::new(message, span))?
        {
            return Ok(Self {
                basename: path.file_name().map(OsString::from),
                path,
                temporary: None,
            });
        }
        let parent = options
            .shared_invocation
            .as_ref()
            .map_or(options.scratch_root.as_path(), InvocationRoot::path);
        let temporary = InvocationRoot::create(parent)
            .map_err(|cause| ExecutionError::caused("create transfer scratch", span, cause))?;
        let path = temporary.path().join("payload");
        let mut material = Self {
            path,
            basename: None,
            temporary: Some(temporary),
        };
        let result = match input {
            Input::Literal(text) => fs::write(&material.path, text.as_bytes())
                .map_err(|cause| ExecutionError::caused("materialize literal", span, cause)),
            Input::File { location, pattern } => crate::runtime::import_finite_source(
                location,
                &material.path,
                pattern.as_deref(),
                options.limits.buffer_bytes,
                options.cancel.clone(),
                span,
            )
            .map(|receipt| {
                material.basename = receipt.basename;
            })
            .map_err(|cause| ExecutionError::caused("receive source", span, cause)),
            Input::Port { .. } => unreachable!("ports retain their flow owner"),
        };
        if let Err(error) = result {
            let _ = material.close();
            return Err(error);
        }
        Ok(material)
    }

    fn close(&mut self) -> Vec<CleanupIssue> {
        let Some(temporary) = self.temporary.take() else {
            return Vec::new();
        };
        temporary
            .cleanup()
            .err()
            .map(|cause| CleanupIssue {
                retained_paths: vec![temporary.path().to_owned()],
                cause: Box::new(cause),
                resolved: false,
            })
            .into_iter()
            .collect()
    }
}
