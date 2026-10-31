//! Structured interpretation shares the ordinary statement publication path.

use std::future::Future;
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::Arc;

use super::{ExecutionError, Result, RuntimeSession};
use crate::endpoint::{Backend, Location};
use crate::hir::control::{
    BinaryOp, ControlInstruction, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource, RangeStage,
    SlotId, TransferInput, TransferOutput,
};
use crate::hir::{self, Instruction, InstructionKind};
use crate::language::DestinationMode;
use crate::relocatable::Namespace;
use crate::syntax::Span;

type Pending<'a, T> = Pin<Box<dyn Future<Output = Result<T>> + Send + 'a>>;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) enum Value {
    Bool(bool),
    String(String),
    Path(Location, DestinationMode),
    Space {
        namespace: Namespace,
        pwd: Option<PathBuf>,
        module_file: Option<PathBuf>,
    },
}

#[derive(Clone, Copy)]
enum PathAccess {
    Entry,
    Target,
}

impl Value {
    fn boolean(self, span: Span) -> Result<bool> {
        match self {
            Self::Bool(value) => Ok(value),
            _ => Err(ExecutionError::new(
                "control condition must have bool type",
                span,
            )),
        }
    }
    fn location(self, span: Span) -> Result<Location> {
        match self {
            Self::Path(location, _) => Ok(location),
            Self::String(path) => Ok(Location {
                backend: Backend::Local,
                path,
            }),
            _ => Err(ExecutionError::new(
                "filesystem operand must be a path or filename string",
                span,
            )),
        }
    }
}

impl RuntimeSession {
    pub(super) fn execute_control<'a>(
        &'a mut self,
        operation: &'a ControlInstruction,
        node: &'a Instruction,
    ) -> Pending<'a, ()> {
        Box::pin(async move {
            let span = node.span;
            match operation {
                ControlInstruction::Let { slot, value } => {
                    let value = self.eval(value, span).await?;
                    self.control_values.insert(*slot, value);
                    Ok(())
                }
                ControlInstruction::Set { slot, value } => {
                    let value = self.eval(value, span).await?;
                    let previous = self.control_values.get_mut(slot).ok_or_else(|| {
                        ExecutionError::new("assignment target is not initialized", span)
                    })?;
                    if std::mem::discriminant(previous) != std::mem::discriminant(&value) {
                        return Err(ExecutionError::new(
                            "assignment cannot change a binding's type",
                            span,
                        ));
                    }
                    *previous = value;
                    Ok(())
                }
                ControlInstruction::If {
                    condition,
                    then_body,
                    else_body,
                } => {
                    let condition = self.eval(condition, span).await?.boolean(span)?;
                    self.control_region(if condition { then_body } else { else_body })
                        .await
                }
                ControlInstruction::For { slot, range, body } => {
                    let mut cursor = Cursor::new(range);
                    while let Some(value) = cursor.next(self, span).await? {
                        self.check(span)?;
                        self.control_values.insert(*slot, value);
                        self.control_region(body).await?;
                        if self.control_break.take().is_some() {
                            break;
                        }
                    }
                    Ok(())
                }
                ControlInstruction::Loop { body } => {
                    self.control_loop(body, span).await?;
                    Ok(())
                }
                ControlInstruction::Break { value } => {
                    let value = match value {
                        Some(value) => Some(self.eval(value, span).await?),
                        None => None,
                    };
                    self.control_break = Some(value);
                    Ok(())
                }
                ControlInstruction::Delete { path } => {
                    let location = self.eval(path, span).await?.location(span)?;
                    self.wait_control_path(&location, PathAccess::Entry, span)
                        .await?;
                    crate::execution::control_fs::delete(&location, &self.cancel, span).await
                }
                ControlInstruction::Spawn {
                    process,
                    executable,
                    arguments,
                    cwd,
                } => {
                    let executable = self.eval(executable, span).await?.location(span)?;
                    let mut values = Vec::with_capacity(arguments.len());
                    for argument in arguments {
                        let value = match self.eval(argument, span).await? {
                            Value::String(value) => hir::Argument::Text(value),
                            Value::Path(location, _) => hir::Argument::Path(location),
                            _ => {
                                return Err(ExecutionError::new(
                                    "program arguments must be strings or paths",
                                    span,
                                ));
                            }
                        };
                        values.push(value);
                    }
                    let cwd = self.eval(cwd, span).await?.location(span)?;
                    if cwd.backend != Backend::Local {
                        return Err(ExecutionError::new(
                            "program working directory must be a local path",
                            span,
                        ));
                    }
                    self.admit_program(
                        *process,
                        node,
                        &executable,
                        &values,
                        std::path::Path::new(&cwd.path),
                    )
                    .await
                }
                ControlInstruction::Transfer {
                    source,
                    destination,
                } => {
                    let source = match source {
                        TransferInput::Scalar(expression) => {
                            match self.eval(expression, span).await? {
                                Value::String(text) => hir::Input::Literal(Arc::from(text)),
                                Value::Path(location, _) => hir::Input::File {
                                    location,
                                    pattern: None,
                                },
                                _ => {
                                    return Err(ExecutionError::new(
                                        "a transfer source must be a path or string",
                                        span,
                                    ));
                                }
                            }
                        }
                        TransferInput::Port { process, port } => hir::Input::Port {
                            process: *process,
                            port: *port,
                        },
                    };
                    let destination = match destination {
                        TransferOutput::File { path, mode } => hir::Output::File {
                            location: self.eval(path, span).await?.location(span)?,
                            mode: *mode,
                        },
                        TransferOutput::Stdin(process) => hir::Output::Stdin(*process),
                    };
                    let instruction = Instruction {
                        id: node.id,
                        kind: InstructionKind::Transfer {
                            source,
                            destination,
                        },
                        span,
                        expansion: node.expansion.clone(),
                    };
                    self.interpret(&hir::Program {
                        body: hir::Region {
                            instructions: vec![instruction],
                        },
                    })
                    .await
                }
            }
        })
    }

    async fn wait_control_path(
        &mut self,
        location: &Location,
        access: PathAccess,
        span: Span,
    ) -> Result<()> {
        if self.flows.is_empty()
            || !matches!(
                location.backend,
                Backend::Local | Backend::Env | Backend::Wsl | Backend::Ssh { .. }
            )
        {
            return Ok(());
        }
        let location = if let Some(path) = crate::runtime::local_path(location, true)
            .map_err(|message| ExecutionError::new(message, span))?
        {
            let path = crate::runtime::ssh::spawn_blocking(move || {
                match (access, path.file_name()) {
                    (PathAccess::Entry, Some(name)) => {
                        let parent = path
                            .parent()
                            .filter(|parent| !parent.as_os_str().is_empty())
                            .unwrap_or_else(|| std::path::Path::new("."));
                        crate::plan::canonical_observation_path(parent).join(name)
                    }
                    _ => crate::plan::canonical_observation_path(&path),
                }
            })
            .await
            .map_err(|cause| {
                ExecutionError::caused("control dependency observation worker failed", span, cause)
            })?;
            Location {
                backend: Backend::Local,
                path: path.to_string_lossy().into_owned(),
            }
        } else {
            location.clone()
        };
        let observed = hir::effects::FileResource::new(location, hir::effects::Scope::Exact);
        let mut index = 0;
        while index < self.flows.len() {
            let conflict = self.flows[index]
                .destination
                .as_ref()
                .is_some_and(|destination| {
                    hir::effects::FileResource::new(destination.clone(), hir::effects::Scope::Exact)
                        .may_overlap(&observed)
                });
            if conflict {
                self.join_flow(index).await?;
            } else {
                index += 1;
            }
        }
        Ok(())
    }

    fn control_region<'a>(&'a mut self, region: &'a hir::Region) -> Pending<'a, ()> {
        Box::pin(async move {
            self.submission = self.submission.checked_add(1).ok_or_else(|| {
                ExecutionError::new("runtime execution epoch exhausted", Span::default())
            })?;
            if !region.instructions.iter().any(|node| {
                matches!(
                    node.kind,
                    InstructionKind::Spawn { .. }
                        | InstructionKind::Control(ControlInstruction::Spawn { .. })
                )
            }) {
                return self.interpret_region(region).await;
            }
            let mut region = region.clone();
            let mut identities = std::collections::BTreeMap::new();
            for node in &region.instructions {
                let process = match &node.kind {
                    InstructionKind::Spawn { process, .. }
                    | InstructionKind::Control(ControlInstruction::Spawn { process, .. }) => {
                        Some(*process)
                    }
                    _ => None,
                };
                if let Some(process) = process {
                    // Serialized/compiler identities grow upward; repeated runtime sites grow downward.
                    let fresh = hir::ProcessId(self.next_dynamic_process);
                    self.next_dynamic_process =
                        self.next_dynamic_process.checked_sub(1).ok_or_else(|| {
                            ExecutionError::new(
                                "dynamic process identity space exhausted",
                                node.span,
                            )
                        })?;
                    identities.insert(process, fresh);
                }
            }
            let local_processes: Vec<_> = identities.values().copied().collect();
            crate::object::control::visit_region(&mut region, &mut ProcessRemap(identities))?;
            self.interpret(&hir::Program { body: region }).await?;
            for process in local_processes {
                if self
                    .programs
                    .get(&process)
                    .is_some_and(|program| program.receipt().is_some())
                {
                    self.programs.remove(&process);
                    self.program_instructions.remove(&process);
                    self.program_leases.remove(&process);
                }
            }
            Ok(())
        })
    }

    fn control_loop<'a>(
        &'a mut self,
        body: &'a hir::Region,
        span: Span,
    ) -> Pending<'a, Option<Value>> {
        Box::pin(async move {
            loop {
                self.check(span)?;
                self.control_region(body).await?;
                if let Some(value) = self.control_break.take() {
                    return Ok(value);
                }
                tokio::task::yield_now().await;
            }
        })
    }

    fn eval<'a>(&'a mut self, expression: &'a Expr, span: Span) -> Pending<'a, Value> {
        Box::pin(async move {
            self.check(span)?;
            match &expression.kind {
                ExprKind::Bool(value) => Ok(Value::Bool(*value)),
                ExprKind::String(value) => Ok(Value::String(value.clone())),
                ExprKind::Path {
                    location,
                    mode,
                    relocation,
                } => {
                    if relocation.is_some() {
                        return Err(ExecutionError::new(
                            "control path was not contextualized",
                            span,
                        ));
                    }
                    Ok(Value::Path(location.clone(), *mode))
                }
                ExprKind::Space(namespace) => Ok(Value::Space {
                    namespace: namespace.clone(),
                    pwd: None,
                    module_file: None,
                }),
                ExprKind::ResolvedSpace {
                    namespace,
                    pwd,
                    module_file,
                } => Ok(Value::Space {
                    namespace: namespace.clone(),
                    pwd: Some(pwd.clone()),
                    module_file: module_file.clone(),
                }),
                ExprKind::Slot(slot) => self.control_values.get(slot).cloned().ok_or_else(|| {
                    ExecutionError::new(
                        format!("runtime binding {} has not been initialized", slot.0),
                        span,
                    )
                }),
                ExprKind::Let { slot, value, body } => {
                    let value = self.eval(value, span).await?;
                    let previous = self.control_values.insert(*slot, value);
                    let result = self.eval(body, span).await;
                    match previous {
                        Some(value) => {
                            self.control_values.insert(*slot, value);
                        }
                        None => {
                            self.control_values.remove(slot);
                        }
                    }
                    result
                }
                ExprKind::Exists(path) => {
                    let location = self.eval(path, span).await?.location(span)?;
                    self.wait_control_path(&location, PathAccess::Target, span)
                        .await?;
                    Ok(Value::Bool(
                        crate::execution::control_fs::exists(&location, &self.cancel, span).await?,
                    ))
                }
                ExprKind::Not(value) => {
                    Ok(Value::Bool(!self.eval(value, span).await?.boolean(span)?))
                }
                ExprKind::Binary { left, op, right } => {
                    let left = self.eval(left, span).await?;
                    if *op == BinaryOp::And && !left.clone().boolean(span)? {
                        return Ok(Value::Bool(false));
                    }
                    if *op == BinaryOp::Or && left.clone().boolean(span)? {
                        return Ok(Value::Bool(true));
                    }
                    let right = self.eval(right, span).await?;
                    let result = match op {
                        BinaryOp::And | BinaryOp::Or => right.boolean(span)?,
                        BinaryOp::Eq => left == right,
                        BinaryOp::Ne => left != right,
                        BinaryOp::Lt | BinaryOp::Le | BinaryOp::Gt | BinaryOp::Ge => {
                            let (Value::String(left), Value::String(right)) = (left, right) else {
                                return Err(ExecutionError::new(
                                    "ordered comparison requires strings",
                                    span,
                                ));
                            };
                            match op {
                                BinaryOp::Lt => left < right,
                                BinaryOp::Le => left <= right,
                                BinaryOp::Gt => left > right,
                                BinaryOp::Ge => left >= right,
                                _ => unreachable!(),
                            }
                        }
                    };
                    Ok(Value::Bool(result))
                }
                ExprKind::Join {
                    base,
                    suffix,
                    mode,
                    suffix_backend,
                } => {
                    let base = self.eval(base, span).await?.location(span)?;
                    if suffix_backend
                        .as_ref()
                        .is_some_and(|backend| *backend != base.backend)
                    {
                        return Err(ExecutionError::new(
                            "path join operands have incompatible endpoints",
                            span,
                        ));
                    }
                    let suffix = self.eval(suffix, span).await?;
                    let suffix = match suffix {
                        Value::String(text) => text,
                        Value::Path(location, _) if location.backend == base.backend => {
                            location.path
                        }
                        _ => {
                            return Err(ExecutionError::new(
                                "path join operands have incompatible endpoints",
                                span,
                            ));
                        }
                    };
                    if suffix.is_empty() && *mode != DestinationMode::Exact {
                        return Ok(Value::Path(base, *mode));
                    }
                    if suffix.is_empty()
                        || suffix.contains('\0')
                        || suffix.starts_with(['/', '\\', '~'])
                        || (suffix.len() > 1 && suffix.as_bytes()[1] == b':')
                        || crate::language::path_boundary_whitespace(&suffix)
                    {
                        return Err(ExecutionError::new(
                            "joined path suffix must be a nonempty relative path without boundary whitespace",
                            span,
                        ));
                    }
                    let separator = if base.path.ends_with(['/', '\\']) {
                        ""
                    } else {
                        "/"
                    };
                    Ok(Value::Path(
                        Location {
                            backend: base.backend,
                            path: format!("{}{separator}{suffix}", base.path),
                        },
                        *mode,
                    ))
                }
                ExprKind::Select { namespace, item } => {
                    let namespace = self.eval(namespace, span).await?;
                    let item = self.eval(item, span).await?;
                    select(namespace, item, span)
                }
                ExprKind::Loop { body } => self.control_loop(body, span).await?.ok_or_else(|| {
                    ExecutionError::new("a value-producing loop exited without a value", span)
                }),
                ExprKind::RangeQuery { range, query } => self.query(range, query, span).await,
            }
        })
    }

    fn range_regex(&mut self, pattern: &str, span: Span) -> Result<regex::Regex> {
        if let Some(regex) = self.range_patterns.get(pattern) {
            return Ok(regex.clone());
        }
        let regex = regex::Regex::new(&format!(r"\A(?:{pattern})\z"))
            .map_err(|cause| ExecutionError::caused("invalid range selector", span, cause))?;
        // Cache only pure automata, never directory entries or existence observations.
        if self.range_patterns.len() == 16 {
            self.range_patterns.pop_first();
        }
        self.range_patterns
            .insert(pattern.to_owned(), regex.clone());
        Ok(regex)
    }

    fn predicate<'a>(
        &'a mut self,
        slot: SlotId,
        expression: &'a Expr,
        value: Value,
        span: Span,
    ) -> Pending<'a, bool> {
        Box::pin(async move {
            let previous = self.control_values.insert(slot, value);
            let result = self
                .eval(expression, span)
                .await
                .and_then(|value| value.boolean(span));
            match previous {
                Some(value) => {
                    self.control_values.insert(slot, value);
                }
                None => {
                    self.control_values.remove(&slot);
                }
            }
            result
        })
    }

    fn query<'a>(
        &'a mut self,
        range: &'a RangeExpr,
        query: &'a RangeQuery,
        span: Span,
    ) -> Pending<'a, Value> {
        Box::pin(async move {
            let mut cursor = Cursor::new(range);
            match query {
                RangeQuery::Empty => Ok(Value::Bool(cursor.next(self, span).await?.is_none())),
                RangeQuery::First => cursor
                    .next(self, span)
                    .await?
                    .ok_or_else(|| ExecutionError::new("first() requires a nonempty range", span)),
                RangeQuery::Last => {
                    let mut last = None;
                    while let Some(value) = cursor.next(self, span).await? {
                        last = Some(value);
                    }
                    last.ok_or_else(|| {
                        ExecutionError::new("last() requires a nonempty range", span)
                    })
                }
                RangeQuery::Any { slot, predicate }
                | RangeQuery::All { slot, predicate }
                | RangeQuery::None { slot, predicate }
                | RangeQuery::Find { slot, predicate } => {
                    while let Some(value) = cursor.next(self, span).await? {
                        let matched = self
                            .predicate(*slot, predicate, value.clone(), span)
                            .await?;
                        match query {
                            RangeQuery::Any { .. } if matched => return Ok(Value::Bool(true)),
                            RangeQuery::All { .. } if !matched => return Ok(Value::Bool(false)),
                            RangeQuery::None { .. } if matched => return Ok(Value::Bool(false)),
                            RangeQuery::Find { .. } if matched => return Ok(value),
                            _ => {}
                        }
                    }
                    match query {
                        RangeQuery::Find { .. } => Err(ExecutionError::new(
                            "find_if() found no matching range item",
                            span,
                        )),
                        RangeQuery::Any { .. } => Ok(Value::Bool(false)),
                        _ => Ok(Value::Bool(true)),
                    }
                }
                RangeQuery::Contains { value } | RangeQuery::FindValue { value } => {
                    let wanted = self.eval(value, span).await?;
                    while let Some(value) = cursor.next(self, span).await? {
                        if value == wanted {
                            return Ok(if matches!(query, RangeQuery::Contains { .. }) {
                                Value::Bool(true)
                            } else {
                                value
                            });
                        }
                    }
                    if matches!(query, RangeQuery::Contains { .. }) {
                        Ok(Value::Bool(false))
                    } else {
                        Err(ExecutionError::new(
                            "find() found no matching range item",
                            span,
                        ))
                    }
                }
            }
        })
    }
}

fn select(namespace: Value, item: Value, span: Span) -> Result<Value> {
    let Value::Space {
        namespace,
        pwd,
        module_file,
    } = namespace
    else {
        return Err(ExecutionError::new(
            "namespace selection requires a space",
            span,
        ));
    };
    let explicit_backend = match &item {
        Value::Path(location, _) => Some(location.backend.clone()),
        _ => None,
    };
    let location = item.location(span)?;
    if let Namespace::Endpoint { backend } = namespace {
        if explicit_backend
            .as_ref()
            .is_some_and(|source| *source != backend)
        {
            return Err(ExecutionError::new(
                "cannot reinterpret a contextual path in a different endpoint",
                span,
            ));
        }
        let path = if backend == Backend::Local && !absolute(&location.path) {
            pwd.ok_or_else(|| ExecutionError::new("local namespace context is unavailable", span))?
                .join(&location.path)
                .to_string_lossy()
                .into_owned()
        } else {
            location.path
        };
        return Ok(Value::Path(
            Location { backend, path },
            DestinationMode::Exact,
        ));
    }
    if explicit_backend
        .as_ref()
        .is_some_and(|backend| *backend != Backend::Local)
    {
        return Err(ExecutionError::new(
            "a remote path cannot be contextualized in a local namespace",
            span,
        ));
    }
    let path = match namespace {
        Namespace::WorkingDirectory => {
            let pwd = pwd.ok_or_else(|| {
                ExecutionError::new("working-directory namespace is unavailable", span)
            })?;
            let suffix = location
                .path
                .strip_prefix("pwd/")
                .or_else(|| location.path.strip_prefix("pwd\\"));
            if location.path == "pwd" {
                pwd
            } else if let Some(suffix) = suffix {
                pwd.join(suffix)
            } else if absolute(&location.path) {
                PathBuf::from(location.path)
            } else {
                pwd.join(location.path)
            }
        }
        Namespace::Module => {
            let file = module_file
                .ok_or_else(|| ExecutionError::new("source-file namespace is unavailable", span))?;
            if location.path == "this" {
                file
            } else if location.path == "parent" {
                file.parent()
                    .ok_or_else(|| ExecutionError::new("source file has no parent", span))?
                    .to_owned()
            } else if let Some(suffix) = location.path.strip_prefix("parent/") {
                file.parent()
                    .ok_or_else(|| ExecutionError::new("source file has no parent", span))?
                    .join(suffix)
            } else {
                return Err(ExecutionError::new(
                    "unknown item in source-file namespace",
                    span,
                ));
            }
        }
        Namespace::Endpoint { .. } => unreachable!(),
    };
    Ok(Value::Path(
        Location {
            backend: Backend::Local,
            path: path.to_string_lossy().into_owned(),
        },
        DestinationMode::Exact,
    ))
}

fn absolute(path: &str) -> bool {
    path.starts_with(['/', '\\', '~']) || (path.len() > 1 && path.as_bytes()[1] == b':')
}

struct Cursor<'plan> {
    kind: CursorKind<'plan>,
}
enum CursorKind<'plan> {
    Source {
        source: &'plan RangeSource,
        ready: Option<std::vec::IntoIter<Location>>,
        index: usize,
    },
    Filter {
        inner: Box<Cursor<'plan>>,
        slot: SlotId,
        expression: &'plan Expr,
    },
    Transform {
        inner: Box<Cursor<'plan>>,
        slot: SlotId,
        expression: &'plan Expr,
    },
    Take {
        inner: Box<Cursor<'plan>>,
        remaining: usize,
    },
    Drop {
        inner: Box<Cursor<'plan>>,
        remaining: usize,
    },
    Reverse {
        inner: Box<Cursor<'plan>>,
        buffer: Option<Vec<Value>>,
    },
    TakeWhile {
        inner: Box<Cursor<'plan>>,
        slot: SlotId,
        expression: &'plan Expr,
        done: bool,
    },
    DropWhile {
        inner: Box<Cursor<'plan>>,
        slot: SlotId,
        expression: &'plan Expr,
        dropping: bool,
    },
    Stride {
        inner: Box<Cursor<'plan>>,
        stride: usize,
        started: bool,
    },
    Unique {
        inner: Box<Cursor<'plan>>,
        previous: Option<Value>,
    },
}

impl<'plan> Cursor<'plan> {
    fn new(range: &'plan RangeExpr) -> Self {
        let mut cursor = Self {
            kind: CursorKind::Source {
                source: &range.source,
                ready: None,
                index: 0,
            },
        };
        for stage in &range.stages {
            let inner = Box::new(cursor);
            let kind = match stage {
                RangeStage::Filter { slot, predicate } => CursorKind::Filter {
                    inner,
                    slot: *slot,
                    expression: predicate,
                },
                RangeStage::Transform { slot, expression } => CursorKind::Transform {
                    inner,
                    slot: *slot,
                    expression,
                },
                RangeStage::Take(count) => CursorKind::Take {
                    inner,
                    remaining: *count,
                },
                RangeStage::Drop(count) => CursorKind::Drop {
                    inner,
                    remaining: *count,
                },
                RangeStage::Reverse => CursorKind::Reverse {
                    inner,
                    buffer: None,
                },
                RangeStage::TakeWhile { slot, predicate } => CursorKind::TakeWhile {
                    inner,
                    slot: *slot,
                    expression: predicate,
                    done: false,
                },
                RangeStage::DropWhile { slot, predicate } => CursorKind::DropWhile {
                    inner,
                    slot: *slot,
                    expression: predicate,
                    dropping: true,
                },
                RangeStage::Stride(stride) => CursorKind::Stride {
                    inner,
                    stride: *stride,
                    started: false,
                },
                RangeStage::Unique => CursorKind::Unique {
                    inner,
                    previous: None,
                },
            };
            cursor = Self { kind };
        }
        cursor
    }

    fn next<'a>(
        &'a mut self,
        session: &'a mut RuntimeSession,
        span: Span,
    ) -> Pending<'a, Option<Value>> {
        Box::pin(async move {
            session.check(span)?;
            match &mut self.kind {
                CursorKind::Source {
                    source,
                    ready,
                    index,
                } => match source {
                    RangeSource::Values(values) => {
                        let Some(expression) = values.get(*index) else {
                            return Ok(None);
                        };
                        *index += 1;
                        Ok(Some(session.eval(expression, span).await?))
                    }
                    RangeSource::Regex { root, pattern } => {
                        if ready.is_none() {
                            let location = session.eval(root, span).await?.location(span)?;
                            session
                                .wait_control_path(&location, PathAccess::Target, span)
                                .await?;
                            let regex = session.range_regex(pattern, span)?;
                            *ready = Some(
                                crate::execution::control_fs::enumerate_regex(
                                    &location,
                                    &regex,
                                    &session.cancel,
                                    span,
                                )
                                .await?
                                .into_iter(),
                            );
                        }
                        Ok(ready
                            .as_mut()
                            .and_then(Iterator::next)
                            .map(|location| Value::Path(location, DestinationMode::Exact)))
                    }
                },
                CursorKind::Filter {
                    inner,
                    slot,
                    expression,
                } => {
                    while let Some(value) = inner.next(session, span).await? {
                        if session
                            .predicate(*slot, expression, value.clone(), span)
                            .await?
                        {
                            return Ok(Some(value));
                        }
                    }
                    Ok(None)
                }
                CursorKind::Transform {
                    inner,
                    slot,
                    expression,
                } => {
                    let Some(value) = inner.next(session, span).await? else {
                        return Ok(None);
                    };
                    let previous = session.control_values.insert(*slot, value);
                    let result = session.eval(expression, span).await;
                    match previous {
                        Some(value) => {
                            session.control_values.insert(*slot, value);
                        }
                        None => {
                            session.control_values.remove(slot);
                        }
                    }
                    result.map(Some)
                }
                CursorKind::Take { inner, remaining } => {
                    if *remaining == 0 {
                        return Ok(None);
                    }
                    *remaining -= 1;
                    inner.next(session, span).await
                }
                CursorKind::Drop { inner, remaining } => {
                    while *remaining > 0 {
                        *remaining -= 1;
                        if inner.next(session, span).await?.is_none() {
                            return Ok(None);
                        }
                    }
                    inner.next(session, span).await
                }
                CursorKind::Reverse { inner, buffer } => {
                    if buffer.is_none() {
                        let mut values = Vec::new();
                        while let Some(value) = inner.next(session, span).await? {
                            values.push(value);
                        }
                        *buffer = Some(values);
                    }
                    Ok(buffer.as_mut().and_then(Vec::pop))
                }
                CursorKind::TakeWhile {
                    inner,
                    slot,
                    expression,
                    done,
                } => {
                    if *done {
                        return Ok(None);
                    }
                    let Some(value) = inner.next(session, span).await? else {
                        *done = true;
                        return Ok(None);
                    };
                    if session
                        .predicate(*slot, expression, value.clone(), span)
                        .await?
                    {
                        Ok(Some(value))
                    } else {
                        *done = true;
                        Ok(None)
                    }
                }
                CursorKind::DropWhile {
                    inner,
                    slot,
                    expression,
                    dropping,
                } => {
                    while let Some(value) = inner.next(session, span).await? {
                        if *dropping
                            && session
                                .predicate(*slot, expression, value.clone(), span)
                                .await?
                        {
                            continue;
                        }
                        *dropping = false;
                        return Ok(Some(value));
                    }
                    Ok(None)
                }
                CursorKind::Stride {
                    inner,
                    stride,
                    started,
                } => {
                    if *stride == 0 {
                        return Err(ExecutionError::new("stride must be positive", span));
                    }
                    if *started {
                        for _ in 1..*stride {
                            if inner.next(session, span).await?.is_none() {
                                return Ok(None);
                            }
                        }
                    }
                    *started = true;
                    inner.next(session, span).await
                }
                CursorKind::Unique { inner, previous } => {
                    while let Some(value) = inner.next(session, span).await? {
                        if previous.as_ref() == Some(&value) {
                            continue;
                        }
                        *previous = Some(value.clone());
                        return Ok(Some(value));
                    }
                    Ok(None)
                }
            }
        })
    }
}

struct ProcessRemap(std::collections::BTreeMap<hir::ProcessId, hir::ProcessId>);
impl crate::object::control::Visitor for ProcessRemap {
    type Error = ExecutionError;
    fn control(&mut self, control: &mut ControlInstruction) -> Result<()> {
        let replace = |process: &mut hir::ProcessId| {
            if let Some(fresh) = self.0.get(process) {
                *process = *fresh;
            }
        };
        match control {
            ControlInstruction::Spawn { process, .. } => replace(process),
            ControlInstruction::Transfer {
                source,
                destination,
            } => {
                if let TransferInput::Port { process, .. } = source {
                    replace(process);
                }
                if let TransferOutput::Stdin(process) = destination {
                    replace(process);
                }
            }
            _ => {}
        }
        Ok(())
    }
    fn instruction(&mut self, node: &mut Instruction) -> Result<()> {
        let replace = |process: &mut hir::ProcessId| {
            if let Some(fresh) = self.0.get(process) {
                *process = *fresh;
            }
        };
        match &mut node.kind {
            InstructionKind::Spawn { process, .. } => replace(process),
            InstructionKind::Await {
                process: Some(process),
            } => replace(process),
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let hir::Input::Port { process, .. } = source {
                    replace(process);
                }
                if let hir::Output::Stdin(process) = destination {
                    replace(process);
                }
            }
            _ => {}
        }
        Ok(())
    }
}
