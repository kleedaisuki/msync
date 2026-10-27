//! Sequential filesystem reference semantics for the structured language.
//!
//! This interpreter is an execution oracle for the effect scheduler, not a
//! replacement for the bounded asynchronous scheduler. Syntax, expansion and
//! graph compilation remain pure. Only this Execute entry point captures bytes.
//! Every implicit transfer has its own transaction; an error in a named region
//! restores that region's origin, including earlier checkpoints. Already finished
//! preceding top-level statements are not rolled back.
//!
//! Program/stream mediation and remote transactional resources remain separate
//! integration obligations and are capability-rejected before execution starts.
//! Finite remote sources are imported at their atomic top-level transfer, not
//! pre-downloaded before earlier statements can publish. Sources that contain the
//! configured scratch domain omit only the identity-checked current invocation.
//! Pre-existing user scratch data remains part of the source. Windows enrollment requires the manager's supported
//! ordinal-insensitive domain policy and rejects case-sensitive NTFS ancestors.

mod domain;
mod error;
mod native;
mod transfer;


use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use tokio_util::sync::CancellationToken;

use crate::hir::{self, Instruction, InstructionKind, Region, RollbackScope, TransactionId};
use crate::language::Isolation;
use crate::scheduler::ResourceLimits;
use crate::syntax::Span;
use crate::transaction::filesystem::{InvocationRoot, Snapshot, SnapshotStore};
use crate::transaction::manager::{CommitReport, Manager};

use domain::Domain;
pub use error::ExecutionError;
use error::Result;

#[derive(Clone, Debug)]
pub struct Options {
    pub scratch_root: PathBuf,
    pub limits: ResourceLimits,
    pub cancel: CancellationToken,
}

impl Options {
    pub fn new(scratch_root: impl Into<PathBuf>) -> Self {
        Self {
            scratch_root: scratch_root.into(),
            limits: ResourceLimits::default(),
            cancel: CancellationToken::new(),
        }
    }

    fn check(&self, span: Span) -> Result<()> {
        if self.cancel.is_cancelled() {
            return Err(ExecutionError::new(
                "execution cancelled before publication",
                span,
            ));
        }
        Ok(())
    }
}

/// Counts assignments and publication boundaries, not physical copy throughput.
#[derive(Debug, Default)]
pub struct ExecutionReport {
    pub assignments: usize,
    pub assigned_files: usize,
    pub assigned_bytes: u64,
    pub checkpoints: usize,
    pub rollbacks: usize,
    pub published_roots: usize,
    pub cleanup: Vec<CleanupIssue>,
}

/// Cleanup after a known commit is a warning, never a claim that it rolled back.
#[derive(Debug)]
pub struct CleanupIssue {
    pub cause: Box<dyn std::error::Error + Send + Sync>,
    pub retained_paths: Vec<PathBuf>,
    pub resolved: bool,
}

/// Execute one complete document against immutable versions of its local domain.
pub fn execute(program: &hir::Program, options: &Options) -> Result<ExecutionReport> {
    domain::validate(program)?;
    options
        .limits
        .validate()
        .map_err(|message| ExecutionError::new(message, Span::default()))?;
    options.check(Span::default())?;
    if program.body.instructions.is_empty() {
        return Ok(ExecutionReport::default());
    }
    if !options.scratch_root.is_absolute() {
        return Err(ExecutionError::new(
            "execution scratch root must be absolute",
            Span::default(),
        ));
    }
    let mut domain = Domain::resolve(program, options)?;
    let mut engine = Engine::new(&domain, options)?;
    let result = engine.region(&program.body, &mut domain, None, program);
    // Imported/source snapshots must release their artifact handles before close.
    drop(domain);
    match result {
        Ok(()) => {
            engine.close();
            Ok(engine.report)
        }
        Err(mut failure) => {
            engine.close();
            for cleanup in engine.report.cleanup {
                failure.retained_paths.extend(cleanup.retained_paths);
                failure.report.help.push(format!(
                    "cleanup{}: {}",
                    if cleanup.resolved { " (recovered)" } else { "" },
                    cleanup.cause
                ));
            }
            failure
                .retained_paths
                .extend(engine.manager.retained_journals());
            if !failure.retained_paths.is_empty() {
                failure.retained_paths.push(engine.workspace);
            }
            Err(failure)
        }
    }
}

struct Engine {
    manager: Manager,
    assets: SnapshotStore,
    workspace: PathBuf,
    invocation: InvocationRoot,
    imports: PathBuf,
    directory: fs::Permissions,
    roots: Vec<PathBuf>,
    options: Options,
    named: BTreeMap<TransactionId, TransactionId>,
    next_transaction: usize,
    report: ExecutionReport,
}

impl Engine {
    fn new(domain: &Domain, options: &Options) -> Result<Self> {
        let invocation = InvocationRoot::create(&options.scratch_root).map_err(|cause| {
            ExecutionError::caused(
                "cannot create owned execution invocation",
                Span::default(),
                cause,
            )
        })?;
        let workspace = invocation.path().to_owned();
        let assets =
            SnapshotStore::new_in(&workspace.join("assets"), &invocation).map_err(|cause| {
                ExecutionError::caused(
                    "cannot create immutable import store",
                    Span::default(),
                    cause,
                )
            })?;
        let manager =
            Manager::new_in(&workspace.join("manager"), &invocation).map_err(|cause| {
                ExecutionError::caused("cannot create transaction manager", Span::default(), cause)
            })?;
        let imports = workspace.join("imports");
        fs::create_dir(&imports).map_err(|cause| {
            ExecutionError::caused("cannot create private imports", Span::default(), cause)
        })?;
        let template = workspace.join("directory-template");
        fs::create_dir(&template).map_err(|cause| {
            ExecutionError::caused("cannot create directory template", Span::default(), cause)
        })?;
        let directory = fs::metadata(&template)
            .map_err(|cause| {
                ExecutionError::caused("cannot inspect directory template", Span::default(), cause)
            })?
            .permissions();
        let mut engine = Self {
            manager,
            assets,
            workspace,
            invocation,
            imports,
            directory,
            roots: domain.roots.clone(),
            options: options.clone(),
            named: BTreeMap::new(),
            next_transaction: 0,
            report: ExecutionReport::default(),
        };
        for root in &domain.roots {
            if let Err(cause) = engine.manager.enroll(root) {
                let mut failure = ExecutionError::caused(
                    format!("cannot enroll resource {}", root.display()),
                    domain.site(root),
                    cause,
                );
                engine.close();
                for cleanup in engine.report.cleanup {
                    failure.retained_paths.extend(cleanup.retained_paths);
                    failure.report.help.push(format!(
                        "cleanup{}: {}",
                        if cleanup.resolved { " (recovered)" } else { "" },
                        cleanup.cause
                    ));
                }
                return Err(failure);
            }
        }
        Ok(engine)
    }

    fn allocate(&mut self) -> Result<TransactionId> {
        let id = TransactionId(self.next_transaction);
        self.next_transaction = self.next_transaction.checked_add(1).ok_or_else(|| {
            ExecutionError::new("runtime transaction identities exhausted", Span::default())
        })?;
        Ok(id)
    }

    fn region(
        &mut self,
        region: &Region,
        domain: &mut Domain,
        current: Option<TransactionId>,
        program: &hir::Program,
    ) -> Result<()> {
        for instruction in &region.instructions {
            self.options.check(instruction.span)?;
            self.instruction(instruction, domain, current, program)?;
        }
        Ok(())
    }

    fn instruction(
        &mut self,
        node: &Instruction,
        domain: &mut Domain,
        current: Option<TransactionId>,
        program: &hir::Program,
    ) -> Result<()> {
        match &node.kind {
            InstructionKind::Transfer { .. } => {
                let transfer = domain.transfers.remove(&node.id).ok_or_else(|| {
                    ExecutionError::new("duplicate or missing transfer identity", node.span)
                })?;
                if let Some(id) = current {
                    self.manager.start_statement(id).map_err(|cause| {
                        ExecutionError::caused("cannot begin statement view", node.span, cause)
                    })?;
                    return self.transfer(node.id, &transfer, id);
                }
                let id = self.allocate()?;
                self.manager
                    .begin(id, Isolation::Serializable)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot begin atomic transfer", node.span, cause)
                    })?;
                let result = self
                    .transfer(node.id, &transfer, id)
                    .and_then(|()| self.finish(id, node.span));
                result.map_err(|mut failure| {
                    self.abort(id, &mut failure);
                    failure
                })
            }
            InstructionKind::Transaction { transaction, body } => {
                if current.is_some() {
                    return Err(ExecutionError::new(
                        "nested named transactions need a nested manager context",
                        node.span,
                    ));
                }
                let isolation = program
                    .transactions
                    .iter()
                    .find(|info| info.id == *transaction)
                    .map(|info| info.isolation)
                    .ok_or_else(|| {
                        ExecutionError::new("transaction metadata is missing", node.span)
                    })?;
                let id = self.allocate()?;
                self.manager.begin(id, isolation).map_err(|cause| {
                    ExecutionError::caused("cannot begin named transaction", node.span, cause)
                })?;
                if self.named.insert(*transaction, id).is_some() {
                    return Err(ExecutionError::new(
                        "transaction identity has more than one declaration",
                        node.span,
                    ));
                }
                let result = self
                    .region(body, domain, Some(id), program)
                    .and_then(|()| self.finish(id, node.span));
                result.map_err(|mut failure| {
                    self.abort(id, &mut failure);
                    failure
                })
            }
            InstructionKind::Commit { transaction } => {
                let id = self.current(*transaction, current, node.span)?;
                let committed = self.manager.checkpoint(id).map_err(|cause| {
                    ExecutionError::caused("checkpoint publication failed", node.span, cause)
                })?;
                self.committed(committed);
                Ok(())
            }
            InstructionKind::Rollback { transaction, scope } => {
                let id = match scope {
                    RollbackScope::Checkpoint => self.current(*transaction, current, node.span)?,
                    RollbackScope::Begin => {
                        if current.is_some() {
                            return Err(ExecutionError::new(
                                "named rollback cannot run inside another transaction",
                                node.span,
                            ));
                        }
                        *self.named.get(transaction).ok_or_else(|| {
                            ExecutionError::new("named rollback has no preceding origin", node.span)
                        })?
                    }
                };
                match scope {
                    RollbackScope::Checkpoint => {
                        self.manager.rollback_pending(id).map_err(|cause| {
                            ExecutionError::caused(
                                "cannot restore latest checkpoint",
                                node.span,
                                cause,
                            )
                        })?
                    }
                    RollbackScope::Begin => {
                        let report = self.manager.rollback_origin(id).map_err(|cause| {
                            ExecutionError::caused(
                                "cannot restore transaction origin",
                                node.span,
                                cause,
                            )
                        })?;
                        self.committed(report);
                    }
                }
                self.report.rollbacks += 1;
                Ok(())
            }
            InstructionKind::Spawn { .. } => unreachable!("capabilities validated"),
        }
    }

    fn current(
        &self,
        named: TransactionId,
        current: Option<TransactionId>,
        span: Span,
    ) -> Result<TransactionId> {
        let id = *self
            .named
            .get(&named)
            .ok_or_else(|| ExecutionError::new("transaction control has no origin", span))?;
        if current != Some(id) {
            return Err(ExecutionError::new(
                "transaction control refers to another context",
                span,
            ));
        }
        Ok(id)
    }

    fn finish(&mut self, id: TransactionId, span: Span) -> Result<()> {
        let committed = self
            .manager
            .finish(id)
            .map_err(|cause| ExecutionError::caused("atomic publication failed", span, cause))?;
        self.committed(committed);
        Ok(())
    }

    fn committed(&mut self, committed: CommitReport) {
        self.report.checkpoints += 1;
        self.report.published_roots += committed.published_roots;
        self.report
            .cleanup
            .extend(committed.cleanup.into_iter().map(|cause| CleanupIssue {
                retained_paths: cause.journal.iter().cloned().collect(),
                cause: Box::new(cause),
                resolved: false,
            }));
    }

    fn abort(&mut self, id: TransactionId, failure: &mut ExecutionError) {
        let recovery = self.manager.recover();
        failure.compensation.extend(recovery);
        if let Err(error) = self.manager.rollback_origin(id) {
            failure.recovery.push(error);
        }
    }

    fn close(&mut self) {
        self.close_inner();
        for issue in &mut self.report.cleanup {
            issue
                .retained_paths
                .retain(|path| path.try_exists().unwrap_or(true));
            issue.resolved = issue.retained_paths.is_empty();
        }
    }

    fn close_inner(&mut self) {
        if let Err(cause) = self.manager.shutdown() {
            self.report.cleanup.push(CleanupIssue {
                retained_paths: vec![self.workspace.clone()],
                cause: Box::new(cause),
                resolved: false,
            });
            return;
        }
        if let Err(cause) = self.assets.cleanup() {
            self.report.cleanup.push(CleanupIssue {
                retained_paths: vec![self.workspace.clone()],
                cause: Box::new(cause),
                resolved: false,
            });
            return;
        }
        if let Err(cause) = self.invocation.cleanup() {
            self.report.cleanup.push(CleanupIssue {
                retained_paths: vec![self.workspace.clone()],
                cause: Box::new(cause),
                resolved: false,
            });
        }
    }

    fn snapshot(
        &self,
        entries: BTreeMap<PathBuf, crate::transaction::filesystem::Entry>,
        span: Span,
    ) -> Result<Snapshot> {
        self.assets.from_entries(entries).map_err(|cause| {
            ExecutionError::caused("cannot assemble immutable assignment", span, cause)
        })
    }

    fn manager_error(
        &self,
        action: &'static str,
        span: Span,
        cause: crate::transaction::manager::ManagerError,
    ) -> ExecutionError {
        ExecutionError::caused(action, span, cause)
    }
}
