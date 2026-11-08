//! Finite ranges copy in selected order; local sources remain borrowed paths.

use super::*;

struct RemoteSources {
    entries: Vec<(PathBuf, OsString)>,
    incoming: Option<InvocationRoot>,
}

impl RemoteSources {
    fn close(&mut self) -> Option<CleanupIssue> {
        let incoming = self.incoming.take()?;
        incoming
            .cleanup()
            .err()
            .map(|cause| super::cleanup(cause, incoming.path().to_owned()))
    }
}

impl Drop for RemoteSources {
    fn drop(&mut self) {
        if let Some(incoming) = self.incoming.take()
            && let Err(cause) = incoming.cleanup()
        {
            crate::telemetry::io_error("copy.incoming.cleanup", &cause.cause);
        }
    }
}

impl Ordinary {
    /// Keep the range as one user operation without making it a disk transaction.
    pub(in crate::execution) fn execute_cohort(
        &mut self,
        nodes: &[Instruction],
    ) -> Result<Attempt> {
        if nodes.first().is_some_and(|node| {
            matches!(&node.kind, InstructionKind::Transfer { destination: Output::File { location, .. }, .. } if matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }))
        }) {
            return self.execute_remote_cohort(nodes);
        }
        let mut ready = Vec::with_capacity(nodes.len());
        for node in nodes {
            self.options.check(node.span)?;
            self.inspect_progress(node);
            let Some(item) = Ready::inspect(node, self.origin.as_ref())? else {
                return Ok(Attempt::Unsupported);
            };
            ready.push(item);
        }
        let Some(first) = ready.first() else {
            return Ok(Attempt::Completed(ExecutionReport::default()));
        };
        if ready
            .iter()
            .all(|item| !matches!(item.source, Source::Remote(_)))
        {
            let mut report = ExecutionReport::default();
            let mut published = std::collections::BTreeSet::new();
            for item in &ready {
                let completed = if let Some(report) = self.execute_file(item)? {
                    report
                } else if let Some(report) = self.execute_directory(item)? {
                    report
                } else {
                    let Attempt::Completed(report) = self.execute_ready(item, None)? else {
                        return Ok(Attempt::Unsupported);
                    };
                    report
                };
                if completed.published_roots != 0 {
                    published.insert(item.target.clone());
                }
                crate::execution::merge(&mut report, completed);
            }
            report.assignments = 1;
            report.published_roots = published.len();
            return Ok(Attempt::Completed(report));
        }
        self.execute_ready(first, Some(&ready))
    }

    fn execute_remote_cohort(&mut self, nodes: &[Instruction]) -> Result<Attempt> {
        let first = &nodes[0];
        let InstructionKind::Transfer { destination, .. } = &first.kind else {
            unreachable!()
        };
        let Output::File {
            location,
            mode: DestinationMode::Container,
        } = destination
        else {
            return Ok(Attempt::Unsupported);
        };
        let mut ready = Vec::with_capacity(nodes.len());
        for node in nodes {
            self.options.check(node.span)?;
            self.inspect_progress(node);
            let InstructionKind::Transfer {
                source,
                destination: output,
            } = &node.kind
            else {
                return Ok(Attempt::Unsupported);
            };
            if output != destination {
                return Err(ExecutionError::new(
                    "a source range needs one destination container",
                    node.span,
                ));
            }
            let Some((source, pattern)) = Source::input(source, node.span)
                .map_err(|error| error.with_operand(self.origin.as_ref(), false))?
            else {
                return Ok(Attempt::Unsupported);
            };
            ready.push(Ready {
                instruction: node.id,
                source,
                target: PathBuf::new(),
                mode: DestinationMode::Container,
                pattern,
                span: node.span,
            });
        }
        let remote = ready
            .iter()
            .map(|item| match &item.source {
                Source::Remote(source)
                    if item.pattern.is_none() && source.backend == location.backend =>
                {
                    Some(source.clone())
                }
                _ => None,
            })
            .collect::<Option<Vec<_>>>();
        if let Some(sources) = remote {
            let mut trace = crate::telemetry::span("copy");
            trace.expect_completion();
            trace.attribute("copy.kind", "remote-cohort");
            trace.attribute("publication.policy", "ordinary-copy");
            trace.metric("instruction.id", first.id.0 as u64);
            let result =
                crate::runtime::ssh_channel::copy_many(&sources, location, &self.options.cancel);
            trace.finish_result(&result);
            let transfer = result.map_err(|message| {
                ExecutionError::new(message, first.span).with_operand(self.origin.as_ref(), true)
            })?;
            return Ok(Attempt::Completed(ExecutionReport {
                assignments: 1,
                assigned_files: transfer.copied_files,
                assigned_bytes: transfer.transferred_bytes,
                published_roots: usize::from(!sources.is_empty()),
                ..ExecutionReport::default()
            }));
        }
        let Some(mut sources) = self.remote_sources(&ready)? else {
            return Ok(Attempt::Unsupported);
        };
        let mut trace = crate::telemetry::span("copy");
        trace.expect_completion();
        trace.attribute("copy.kind", "remote-cohort");
        trace.attribute("publication.policy", "ordinary-copy");
        trace.metric("instruction.id", first.id.0 as u64);
        let result = crate::runtime::ssh_channel::upload_many(
            &sources.entries,
            location,
            self.options
                .shared_invocation
                .as_ref()
                .or(sources.incoming.as_ref()),
            &self.options.cancel,
        )
        .map_err(|message| {
            ExecutionError::new(message, first.span).with_operand(self.origin.as_ref(), true)
        });
        trace.finish_result(&result);
        let cleanup = sources.close();
        let transfer = result?;
        let mut report = ExecutionReport {
            assignments: 1,
            assigned_files: transfer.copied_files,
            assigned_bytes: transfer.transferred_bytes,
            published_roots: usize::from(!sources.entries.is_empty()),
            ..ExecutionReport::default()
        };
        report.cleanup.extend(cleanup);
        Ok(Attempt::Completed(report))
    }

    fn remote_sources(&self, ready: &[Ready]) -> Result<Option<RemoteSources>> {
        use super::receive::Incoming;
        let mut sources = RemoteSources {
            entries: Vec::new(),
            incoming: None,
        };
        for (index, item) in ready.iter().enumerate() {
            self.options.check(item.span)?;
            match &item.source {
                Source::Local(path) => {
                    append_selected_sources(
                        path,
                        item.pattern.as_deref(),
                        &mut sources.entries,
                        item.span,
                    )?;
                }
                Source::Remote(location) => {
                    if sources.incoming.is_none() {
                        let scratch = self
                            .options
                            .shared_invocation
                            .as_ref()
                            .map_or(self.options.scratch_root.as_path(), InvocationRoot::path);
                        sources.incoming =
                            Some(InvocationRoot::create(scratch).map_err(|cause| {
                                failure("cannot create incoming source stage", item.span, cause)
                            })?);
                    }
                    let payload = sources
                        .incoming
                        .as_ref()
                        .expect("incoming owner initialized")
                        .path()
                        .join(format!("source-{index}"));
                    let Incoming::Captured { basename } =
                        self.receive_source(location, &payload, item)?
                    else {
                        return Ok(None);
                    };
                    if item.pattern.is_some() {
                        append_selected_sources(
                            &payload,
                            Some("(?s:.*)"),
                            &mut sources.entries,
                            item.span,
                        )?;
                    } else {
                        sources.entries.push((
                            payload,
                            basename.ok_or_else(|| {
                                ExecutionError::new(
                                    "a container source needs a basename",
                                    item.span,
                                )
                            })?,
                        ));
                    }
                }
                Source::Literal(_) => {
                    return Err(ExecutionError::new(
                        "a literal has no basename for a container",
                        item.span,
                    ));
                }
            }
        }
        Ok(Some(sources))
    }
}

fn append_selected_sources(
    source: &Path,
    pattern: Option<&str>,
    entries: &mut Vec<(PathBuf, OsString)>,
    span: Span,
) -> Result<()> {
    let Some(pattern) = pattern else {
        entries.push((
            source.to_owned(),
            source
                .file_name()
                .ok_or_else(|| ExecutionError::new("a container source needs a basename", span))?
                .to_owned(),
        ));
        return Ok(());
    };
    let regex = regex::Regex::new(&format!("\\A(?:{pattern})\\z"))
        .map_err(|cause| ExecutionError::caused("invalid source selector", span, cause))?;
    let mut children = fs::read_dir(source)
        .map_err(|cause| ExecutionError::caused("cannot enumerate selected source", span, cause))?
        .collect::<io::Result<Vec<_>>>()
        .map_err(|cause| ExecutionError::caused("cannot read selected source", span, cause))?;
    children.sort_by_key(fs::DirEntry::file_name);
    for child in children {
        let name = child.file_name();
        if name.to_str().is_some_and(|name| regex.is_match(name)) {
            entries.push((child.path(), name));
        }
    }
    Ok(())
}

impl Storage {
    pub(super) fn execute_cohort(
        &mut self,
        ready: &[Ready],
        options: &Options,
        progress: Option<&Progress>,
        origin: Option<&crate::source::InstructionOrigin>,
    ) -> Result<Attempt> {
        let mut staged = Staged {
            targets: Some(BTreeMap::new()),
            ..Default::default()
        };
        for item in ready {
            if !self.stage(item, options, progress, origin, &mut staged)? {
                return Ok(Attempt::Unsupported);
            }
        }
        self.publish(
            &staged.assignments,
            &ready[0],
            options,
            progress,
            &mut staged.report,
        )?;
        staged.report.assignments = 1;
        Ok(Attempt::Completed(staged.report))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn local_cohort_borrows_original_paths_without_copying_payloads_or_creating_scratch() {
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let root = tempfile::tempdir_in(base).unwrap();
        let scratch = root.path().join("unused-scratch");
        let owner = Ordinary::new(&Options::new(&scratch));
        let mut inputs = Vec::new();
        for name in ["a", "b"] {
            let path = root.path().join(name);
            fs::write(&path, name.as_bytes()).unwrap();
            inputs.push(Ready {
                instruction: InstructionId(1),
                source: Source::Local(path),
                target: PathBuf::new(),
                mode: DestinationMode::Container,
                pattern: None,
                span: Span::default(),
            });
        }
        let mut sources = owner.remote_sources(&inputs).unwrap().unwrap();
        assert_eq!(sources.entries.len(), 2);
        assert!(sources.incoming.is_none());
        for (path, name) in &sources.entries {
            assert_eq!(path, &root.path().join(name));
            assert!(same_file::is_same_file(path, root.path().join(name)).unwrap());
        }
        assert!(sources.close().is_none());
        assert!(!scratch.exists());
        assert!(owner.storage.is_none());
    }
}
