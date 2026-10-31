//! Finite range capture and publication share one statement decision.

use super::*;

struct CapturedCohort {
    path: PathBuf,
    origins: Vec<(Location, String)>,
    report: ExecutionReport,
}

impl Ordinary {
    /// One finite source range is one publication decision, not a loop of writes.
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
        self.execute_ready(first, Some(&ready))
    }

    fn execute_remote_cohort(&mut self, nodes: &[Instruction]) -> Result<Attempt> {
        let first = &nodes[0];
        let InstructionKind::Transfer { destination, .. } = &first.kind else {
            unreachable!("cohort classified by its first transfer");
        };
        let Output::File {
            mode: DestinationMode::Container,
            ..
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
                    "a remote finite cohort requires one destination container",
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
        if self.storage.is_none() {
            self.storage = Some(Storage::new(&self.options, first.span)?);
        }
        let Some(mut captured) = self
            .storage
            .as_mut()
            .expect("ordinary storage initialized")
            .capture_cohort(&mut ready, &self.options, self.origin.as_ref())?
        else {
            return Ok(Attempt::Unsupported);
        };
        let source = Input::File {
            location: Location {
                backend: Backend::Local,
                path: captured.path.to_string_lossy().into_owned(),
            },
            pattern: Some("(?s:.*)".into()),
        };
        let spec = crate::execution::endpoint::inspect(&source, destination, first.span)?
            .expect("remote output inspected before acquisition")
            .with_cohort_origins(captured.origins);
        let executor = tokio::runtime::Handle::try_current().map_err(|cause| {
            ExecutionError::caused(
                "remote cohort requires the execution worker runtime",
                first.span,
                cause,
            )
        })?;
        let mut trace = crate::telemetry::span("copy");
        trace.expect_completion();
        trace.attribute("copy.kind", "remote-cohort");
        trace.attribute("publication.policy", "statement-journal");
        trace.metric("instruction.id", first.id.0 as u64);
        let result = executor.block_on(spec.run(
            captured.path,
            None,
            self.options.limits.buffer_bytes,
            self.options.cancel.clone(),
            self.options.scratch_root.clone(),
        ));
        trace.finish_result(&result);
        let remote = result.map_err(|error| error.with_operand(self.origin.as_ref(), true))?;
        captured.report.assignments = 1;
        captured.report.published_roots = 1;
        captured.report.cleanup.extend(remote.cleanup);
        captured.report.cleanup.extend(self.close());
        if let Some(context) = crate::telemetry::context() {
            context.event(
                "remote.publication",
                &[],
                &[
                    (
                        "transport.upload.files",
                        remote.transfer.copied_files as u64,
                    ),
                    ("transport.upload.bytes", remote.transfer.transferred_bytes),
                ],
            );
        }
        Ok(Attempt::Completed(captured.report))
    }
}

impl Storage {
    fn capture_cohort(
        &mut self,
        ready: &mut [Ready],
        options: &Options,
        origin: Option<&crate::source::InstructionOrigin>,
    ) -> Result<Option<CapturedCohort>> {
        let span = ready[0].span;
        let path = self.fresh("cohort");
        let mut children: BTreeMap<OsString, Snapshot> = BTreeMap::new();
        let mut origins = Vec::new();
        let mut report = ExecutionReport::default();
        for item in ready {
            item.target.clone_from(&path);
            options.check(item.span)?;
            let Some(source) = self
                .source(item, options)
                .map_err(|error| error.with_operand(origin, false))?
            else {
                return Ok(None);
            };
            let selections = self
                .select(source, item.pattern.as_deref(), item.span, options)
                .map_err(|error| error.with_operand(origin, false))?;
            for selection in selections {
                let basename = selection.basename.as_ref().ok_or_else(|| {
                    ExecutionError::new(
                        "a literal has no basename for a remote container",
                        item.span,
                    )
                })?;
                let name = basename.to_str().ok_or_else(|| {
                    ExecutionError::new("remote source basename is not UTF-8", item.span)
                })?;
                if matches!(name, "" | "." | "..") || name.contains(['/', '\\', '\0']) {
                    return Err(ExecutionError::new(
                        "invalid remote source basename",
                        item.span,
                    ));
                }
                if let Some(location) = selection_origin(item, &selection, name) {
                    origins.push((location, name.to_owned()));
                }
                let assigned = match children.get(basename) {
                    Some(previous) => self.assignment_over(&selection.snapshot, previous, item)?,
                    None => selection.snapshot.clone(),
                };
                for entry in selection.snapshot.entries().values() {
                    if let Entry::File { artifact, .. } = entry {
                        report.assigned_files += 1;
                        report.assigned_bytes =
                            report.assigned_bytes.saturating_add(artifact.len());
                    }
                }
                children.insert(basename.clone(), assigned);
            }
        }
        let metadata = NodeMetadata::capture(&fs::metadata(&self.imports).map_err(|cause| {
            ExecutionError::caused("cannot inspect aggregate directory", span, cause)
        })?)
        .map_err(|cause| {
            ExecutionError::caused("cannot capture aggregate directory metadata", span, cause)
        })?;
        let mut entries = BTreeMap::new();
        entries.insert(PathBuf::new(), Entry::Directory { metadata });
        for (name, snapshot) in children {
            let prefix = Path::new(&name);
            for (relative, entry) in snapshot.entries() {
                entries.insert(prefix.join(relative), entry.clone());
            }
        }
        let snapshot = self.snapshot(entries, span)?;
        let mut publication = self
            .assets
            .prepare(&snapshot, &path)
            .map_err(|cause| failure("cannot prepare owned remote aggregate", span, cause))?;
        publication
            .publish()
            .map_err(|cause| failure("cannot materialize owned remote aggregate", span, cause))?;
        publication
            .finalize()
            .map_err(|cause| failure("cannot retire owned aggregate staging", span, cause))?;
        Ok(Some(CapturedCohort {
            path,
            origins,
            report,
        }))
    }

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

fn selection_origin(ready: &Ready, selection: &Selection, basename: &str) -> Option<Location> {
    if let Some(source) = &selection.source {
        return Some(Location {
            backend: Backend::Local,
            path: source.to_string_lossy().into_owned(),
        });
    }
    let Source::Remote(location) = &ready.source else {
        return None;
    };
    let mut location = location.clone();
    if ready.pattern.is_some() {
        if !location.path.ends_with('/') {
            location.path.push('/');
        }
        location.path.push_str(basename);
    }
    Some(location)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn remote_aggregate_keeps_named_payloads_and_original_alias_proofs() {
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let root = tempfile::Builder::new()
            .prefix("remote-aggregate-")
            .tempdir_in(base)
            .unwrap();
        let options = Options::new(root.path().join("scratch"));
        let mut owner = Ordinary::new(&options);
        owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
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
        let aggregate = owner
            .storage
            .as_mut()
            .unwrap()
            .capture_cohort(&mut inputs, &options, None)
            .unwrap()
            .unwrap();
        assert!(
            owner
                .storage
                .as_ref()
                .unwrap()
                .invocation
                .contains_private(&aggregate.path)
                .unwrap()
        );
        assert_eq!(aggregate.report.assigned_files, 2);
        assert_eq!(aggregate.origins.len(), 2);
        for (index, name) in ["a", "b"].into_iter().enumerate() {
            assert_eq!(
                fs::read(aggregate.path.join(name)).unwrap(),
                name.as_bytes()
            );
            assert_eq!(
                aggregate.origins[index],
                (
                    Location {
                        backend: Backend::Local,
                        path: root.path().join(name).to_string_lossy().into_owned()
                    },
                    name.to_owned()
                )
            );
            assert_eq!(fs::read(root.path().join(name)).unwrap(), name.as_bytes());
        }
        assert!(owner.close().is_empty());
        assert!(!aggregate.path.exists());
    }
}
