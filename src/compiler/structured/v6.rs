//! Typed scalar lowering; filesystem predicates are never compile-time observations.
use super::*;
use crate::hir::control as ctl;
use std::sync::atomic::Ordering;

pub(super) struct LoopFrame {
    types: Vec<Option<ctl::Type>>,
    pending: BTreeSet<ProcessId>,
}

impl Scope {
    pub(super) fn slot(&self) -> ctl::SlotId {
        ctl::SlotId(self.slots.fetch_add(1, Ordering::Relaxed))
    }
}

pub(super) fn visible_values(scope: &Scope) -> Arc<imbl::OrdMap<String, Value>> {
    symbols::captured(scope.globals.as_ref(), &scope.values)
}

pub(super) fn visible_sites(scope: &Scope) -> Arc<imbl::OrdMap<String, SourceSpan>> {
    symbols::captured(scope.global_sites.as_ref(), &scope.sites)
}

pub(super) fn visible_expansions(scope: &Scope) -> Arc<ExpansionMap> {
    symbols::captured(scope.global_expansions.as_ref(), &scope.expansions)
}

pub(super) fn is_bool(value: &Value) -> bool {
    matches!(
        value,
        Value::Bool(_)
            | Value::Dynamic(ctl::Expr {
                ty: ctl::Type::Bool,
                ..
            })
    )
}

pub(super) fn check_assignment(op: BindingOp, value: &Value, span: Span) -> Result<(), Report> {
    if op == BindingOp::Assign
        && !matches!(
            value,
            Value::Bool(_)
                | Value::Literal(_)
                | Value::Dynamic(ctl::Expr {
                    ty: ctl::Type::Bool | ctl::Type::String,
                    ..
                })
        )
    {
        return Err(Report::new("'=' binds only bool or string values", span)
            .help("Use ':=' for paths/ranges, or '=>' for spaces and programs."));
    }
    Ok(())
}

pub(super) fn check_reassignment(
    name: &Name,
    value: &Value,
    scope: &Scope,
    context: &CompileContext,
) -> Result<(), Failure> {
    let Some(previous) = scope.values.get(&name.text) else {
        return Ok(());
    };
    let old = scalar(previous.clone(), context, name.span)?;
    let new = scalar(value.clone(), context, name.span)?;
    if old.ty != new.ty || !matches!(old.ty, ctl::Type::Bool | ctl::Type::String) {
        return Err(Report::new(
            "'=' reassignment must preserve the existing bool or string type",
            name.span,
        )
        .into());
    }
    Ok(())
}

fn assigned_names(body: &[language::Statement], names: &mut BTreeSet<String>) {
    use language::StatementKind as S;
    for statement in body {
        match &statement.kind {
            S::Binding { name, op, value } => {
                if *op == BindingOp::Assign {
                    names.insert(name.text.clone());
                }
                if let ExprKind::Loop { body } = &value.kind {
                    assigned_names(body, names);
                }
            }
            S::If {
                then_body,
                else_body,
                ..
            } => {
                assigned_names(then_body, names);
                assigned_names(else_body, names);
            }
            S::For { body, .. } | S::Loop { body } => assigned_names(body, names),
            S::Scheduled { statement, .. } => {
                assigned_names(std::slice::from_ref(statement.as_ref()), names)
            }
            _ => {}
        }
    }
}

pub(super) fn drop_bindings(names: &[Name], scope: &mut Scope) -> Result<(), Report> {
    for name in names {
        check_name(name)?;
        if !scope.values.contains_key(&name.text)
            && !scope.macros.contains_key(&name.text)
            && !scope.symbols.contains_key(&name.text)
        {
            return Err(Report::new(
                format!("unknown binding '{}' in drop", name.text),
                name.span,
            ));
        }
        Arc::make_mut(&mut scope.values).remove(&name.text);
        Arc::make_mut(&mut scope.macros).remove(&name.text);
        Arc::make_mut(&mut scope.symbols).remove(&name.text);
        Arc::make_mut(&mut scope.sites).remove(&name.text);
        Arc::make_mut(&mut scope.expansions).remove(&name.text);
        Arc::make_mut(&mut scope.defined_units).remove(&name.text);
        scope.imported.remove(&name.text);
        scope.cells.remove(&name.text);
        scope.local_templates.remove(&name.text);
    }
    Ok(())
}

pub(super) fn scalar(
    value: Value,
    context: &CompileContext,
    span: Span,
) -> Result<ctl::Expr, Failure> {
    let (ty, kind) = match value {
        Value::Bool(value) => (ctl::Type::Bool, ctl::ExprKind::Bool(value)),
        Value::Literal(value) => (ctl::Type::String, ctl::ExprKind::String(value.to_string())),
        Value::Path(value) => {
            let mode = value.mode;
            let (location, relocation) = location(value, context, span)?;
            (
                ctl::Type::Path,
                ctl::ExprKind::Path {
                    location,
                    mode,
                    relocation,
                },
            )
        }
        Value::Space(value) => (ctl::Type::Space, ctl::ExprKind::Space(namespace(&value))),
        Value::Dynamic(value) => return Ok(value),
        _ => {
            return Err(Report::new(
                "this value is not a scalar bool, string, path or space",
                span,
            )
            .into());
        }
    };
    Ok(ctl::Expr { ty, kind })
}

pub(super) fn filename(value: ctl::Expr, span: Span) -> Result<ctl::Expr, Report> {
    require_path_or_string(&value, span)?;
    if value.ty == ctl::Type::Path {
        return Ok(value);
    }
    Ok(ctl::Expr {
        ty: ctl::Type::Path,
        kind: ctl::ExprKind::Select {
            namespace: Box::new(ctl::Expr {
                ty: ctl::Type::Space,
                kind: ctl::ExprKind::Space(Namespace::Endpoint {
                    backend: Backend::Local,
                }),
            }),
            item: Box::new(value),
        },
    })
}

pub(super) fn resolve_expression(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    match &expression.kind {
        ExprKind::Bool(value) => Ok(Value::Bool(*value)),
        ExprKind::Not(operand) => {
            let value = resolve(operand, scope, context)?;
            require_bool(&value, operand.span)?;
            if let Value::Bool(value) = value {
                return Ok(Value::Bool(!value));
            }
            Ok(Value::Dynamic(ctl::Expr {
                ty: ctl::Type::Bool,
                kind: ctl::ExprKind::Not(Box::new(scalar(value, context, operand.span)?)),
            }))
        }
        ExprKind::Exists(operand) => {
            let value = scalar(resolve(operand, scope, context)?, context, operand.span)?;
            let value = filename(value, operand.span)?;
            Ok(Value::Dynamic(ctl::Expr {
                ty: ctl::Type::Bool,
                kind: ctl::ExprKind::Exists(Box::new(value)),
            }))
        }
        ExprKind::Binary {
            left,
            op: language::BinaryOp::Pipe,
            right,
            ..
        } => ranges::resolve_pipe(left, right, scope, context),
        ExprKind::Binary {
            left,
            op,
            right,
            operator,
        } => {
            let left_value = resolve(left, scope, context)?;
            let right_value = resolve(right, scope, context)?;
            binary(left_value, *op, right_value, context, *operator)
        }
        ExprKind::Loop { .. } => Err(Report::new(
            "a loop expression requires an executable statement context",
            expression.span,
        )
        .into()),
        _ => unreachable!("v0.6 expression dispatch"),
    }
}

pub(super) fn require_bool(value: &Value, span: Span) -> Result<(), Report> {
    if is_bool(value) {
        return Ok(());
    }
    Err(Report::new("this expression must have bool type", span)
        .help("There is no implicit truthiness conversion; use true/false, exists, comparison or a boolean function."))
}

fn require_path_or_string(value: &ctl::Expr, span: Span) -> Result<(), Report> {
    if matches!(value.ty, ctl::Type::Path | ctl::Type::String) {
        return Ok(());
    }
    Err(Report::new(
        "expected a path expression or filename string",
        span,
    ))
}

fn binary(
    left: Value,
    op: language::BinaryOp,
    right: Value,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    use language::BinaryOp as B;
    if matches!(op, B::And | B::Or) {
        require_bool(&left, span)?;
        require_bool(&right, span)?;
        match (&left, &right, op) {
            (Value::Bool(false), _, B::And) => return Ok(Value::Bool(false)),
            (Value::Bool(true), _, B::Or) => return Ok(Value::Bool(true)),
            (Value::Bool(a), Value::Bool(b), _) => {
                return Ok(Value::Bool(if op == B::And { *a && *b } else { *a || *b }));
            }
            _ => {}
        }
    }
    let left = scalar(left, context, span)?;
    let right = scalar(right, context, span)?;
    if left.ty != right.ty || !matches!(left.ty, ctl::Type::Bool | ctl::Type::String) {
        return Err(Report::new(
            "comparison operands must have the same bool or string type",
            span,
        )
        .into());
    }
    if left.ty == ctl::Type::Bool && !matches!(op, B::And | B::Or | B::Eq | B::Ne) {
        return Err(Report::new("bool supports only and, or, == and !=", span).into());
    }
    let ordering = match (&left.kind, &right.kind) {
        (ctl::ExprKind::String(a), ctl::ExprKind::String(b)) => Some(a.cmp(b)),
        (ctl::ExprKind::Bool(a), ctl::ExprKind::Bool(b)) => Some(a.cmp(b)),
        _ => None,
    };
    if let Some(ordering) = ordering {
        let result = match op {
            B::Eq => ordering.is_eq(),
            B::Ne => !ordering.is_eq(),
            B::Lt => ordering.is_lt(),
            B::Le => !ordering.is_gt(),
            B::Gt => ordering.is_gt(),
            B::Ge => !ordering.is_lt(),
            _ => unreachable!(),
        };
        return Ok(Value::Bool(result));
    }
    let op = match op {
        B::And => ctl::BinaryOp::And,
        B::Or => ctl::BinaryOp::Or,
        B::Eq => ctl::BinaryOp::Eq,
        B::Ne => ctl::BinaryOp::Ne,
        B::Lt => ctl::BinaryOp::Lt,
        B::Le => ctl::BinaryOp::Le,
        B::Gt => ctl::BinaryOp::Gt,
        B::Ge => ctl::BinaryOp::Ge,
        B::Pipe => unreachable!(),
    };
    Ok(Value::Dynamic(ctl::Expr {
        ty: ctl::Type::Bool,
        kind: ctl::ExprKind::Binary {
            left: Box::new(left),
            op,
            right: Box::new(right),
        },
    }))
}

pub(super) fn join(
    base: Value,
    suffix: Option<Value>,
    mode: DestinationMode,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    if matches!(&base, Value::Path(path) if path.is_file) {
        return Err(
            Report::new("a known file value cannot have child path components", span).into(),
        );
    }
    let base = filename(scalar(base, context, span)?, span)?;
    let (suffix, suffix_backend) = match suffix.unwrap_or_else(|| Value::Literal(Arc::from(""))) {
        Value::Path(path) if path.relocation.is_none() => (
            ctl::Expr {
                ty: ctl::Type::String,
                kind: ctl::ExprKind::String(path.path),
            },
            path.backend,
        ),
        value => (scalar(value, context, span)?, None),
    };
    require_path_or_string(&base, span)?;
    require_path_or_string(&suffix, span)?;
    Ok(Value::Dynamic(ctl::Expr {
        ty: ctl::Type::Path,
        kind: ctl::ExprKind::Join {
            base: Box::new(base),
            suffix: Box::new(suffix),
            mode,
            suffix_backend,
        },
    }))
}

pub(super) fn select(
    namespace: Value,
    item: Value,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    let namespace = scalar(namespace, context, span)?;
    let item = scalar(item, context, span)?;
    if namespace.ty != ctl::Type::Space {
        return Err(Report::new("left operand of '::' must be a space", span).into());
    }
    require_path_or_string(&item, span)?;
    Ok(Value::Dynamic(ctl::Expr {
        ty: ctl::Type::Path,
        kind: ctl::ExprKind::Select {
            namespace: Box::new(namespace),
            item: Box::new(item),
        },
    }))
}

impl Lower<'_> {
    fn control_emit(
        &mut self,
        region: &mut Region,
        span: Span,
        instruction: ctl::ControlInstruction,
    ) {
        self.emit(region, span, InstructionKind::Control(instruction));
    }

    fn promote_assignments(
        &mut self,
        bodies: &[&[language::Statement]],
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
        span: Span,
    ) -> Result<(), Failure> {
        let mut names = BTreeSet::new();
        for body in bodies {
            assigned_names(body, &mut names);
        }
        for name in names {
            if scope.cells.contains_key(&name) {
                continue;
            }
            let Some(value) = scope.values.get(&name).cloned() else {
                continue;
            };
            let value = scalar(value, context, span)?;
            if !matches!(value.ty, ctl::Type::Bool | ctl::Type::String) {
                continue;
            }
            let slot = scope.slot();
            let ty = value.ty.clone();
            self.control_emit(region, span, ctl::ControlInstruction::Let { slot, value });
            Arc::make_mut(&mut scope.values).insert(
                name.clone(),
                Value::Dynamic(ctl::Expr {
                    ty,
                    kind: ctl::ExprKind::Slot(slot),
                }),
            );
            scope.cells.insert(name, slot);
        }
        Ok(())
    }

    fn prepare_expression(
        &mut self,
        expression: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), Failure> {
        if let ExprKind::Loop { body } = &expression.kind {
            self.promote_assignments(&[body], scope, context, region, expression.span)?;
        }
        Ok(())
    }

    pub(super) fn control(
        &mut self,
        statement: &language::Statement,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        use language::StatementKind as S;
        match &statement.kind {
            S::Drop(names) => drop_bindings(names, scope).map_err(|e| self.located(e)),
            S::Delete(path) => {
                self.prepare_expression(path, scope, context, region)
                    .map_err(|e| self.located(e))?;
                let value = self
                    .executable_value(path, scope, context)
                    .and_then(|value| scalar(value, context, path.span))
                    .map_err(|e| self.located(e))?;
                let value = filename(value, path.span).map_err(|e| self.located(e))?;
                self.control_emit(
                    region,
                    statement.span,
                    ctl::ControlInstruction::Delete { path: value },
                );
                Ok(())
            }
            S::If {
                condition,
                then_body,
                else_body,
            } => {
                self.prepare_expression(condition, scope, context, region)
                    .map_err(|e| self.located(e))?;
                let value = self
                    .executable_value(condition, scope, context)
                    .map_err(|e| self.located(e))?;
                require_bool(&value, condition.span).map_err(|e| self.located(e))?;
                if !matches!(value, Value::Bool(_)) {
                    self.promote_assignments(
                        &[then_body, else_body],
                        scope,
                        context,
                        region,
                        statement.span,
                    )
                    .map_err(|e| self.located(e))?;
                }
                // Both arms are typechecked, even when the condition folds.
                let before_pending = self.pending.clone();
                let before_consumed = self.session.consumed.clone();
                let origin_start = hir::InstructionId(self.next_instruction);
                let before_relocations = self.relocations.len();
                let mut then_scope = scope.clone();
                let mut else_scope = scope.clone();
                let mut then_region = Region::default();
                let mut else_region = Region::default();
                if let Some(error) = self
                    .block(then_body, &mut then_scope, context, &mut then_region)
                    .into_iter()
                    .next()
                {
                    return Err(error);
                }
                self.finish_lexical_region(&mut then_region, &before_pending, statement.span);
                let then_consumed = self.session.consumed.clone();
                let then_origins = self.origins.split_off(&origin_start);
                let then_relocations = self.relocations.split_off(before_relocations);
                self.pending = before_pending.clone();
                self.session.consumed = before_consumed.clone();
                if let Some(error) = self
                    .block(else_body, &mut else_scope, context, &mut else_region)
                    .into_iter()
                    .next()
                {
                    return Err(error);
                }
                self.finish_lexical_region(&mut else_region, &before_pending, statement.span);
                self.pending = before_pending;
                match value {
                    Value::Bool(true) => {
                        Self::commit_lexical_updates(scope, &then_scope, then_body);
                        self.session.consumed = then_consumed;
                        drop(self.origins.split_off(&origin_start));
                        self.origins.extend(then_origins);
                        self.relocations.truncate(before_relocations);
                        self.relocations.extend(then_relocations);
                        region.instructions.extend(then_region.instructions);
                    }
                    Value::Bool(false) => {
                        Self::commit_lexical_updates(scope, &else_scope, else_body);
                        region.instructions.extend(else_region.instructions);
                    }
                    value => {
                        self.session.consumed.extend(then_consumed);
                        self.origins.extend(then_origins);
                        self.relocations.extend(then_relocations);
                        let condition =
                            scalar(value, context, condition.span).map_err(|e| self.located(e))?;
                        self.control_emit(
                            region,
                            statement.span,
                            ctl::ControlInstruction::If {
                                condition,
                                then_body: then_region,
                                else_body: else_region,
                            },
                        );
                    }
                }
                Ok(())
            }
            S::For { name, range, body } => {
                self.promote_assignments(&[body], scope, context, region, statement.span)
                    .map_err(|e| self.located(e))?;
                check_name(name).map_err(|e| self.located(e))?;
                let value = resolve(range, scope, context).map_err(|e| self.located(e))?;
                let Value::Range(range) = value else {
                    return Err(
                        self.located(Report::new("for requires a range expression", range.span))
                    );
                };
                let slot = scope.slot();
                let mut inner = scope.clone();
                inner.cells.remove(&name.text);
                Arc::make_mut(&mut inner.values).insert(
                    name.text.clone(),
                    Value::Dynamic(ctl::Expr {
                        ty: range.item_type.clone(),
                        kind: ctl::ExprKind::Slot(slot),
                    }),
                );
                let (body, ty) = self
                    .loop_region(body, &mut inner, context, false)
                    .map_err(|e| self.located(e))?;
                if ty.is_some() {
                    return Err(self.located(Report::new(
                        "for loops accept only bare break; use loop to return a value",
                        statement.span,
                    )));
                }
                self.control_emit(
                    region,
                    statement.span,
                    ctl::ControlInstruction::For { slot, range, body },
                );
                Ok(())
            }
            S::Loop { body } => {
                self.promote_assignments(&[body], scope, context, region, statement.span)
                    .map_err(|e| self.located(e))?;
                let (body, _) = self
                    .loop_region(body, &mut scope.clone(), context, false)
                    .map_err(|e| self.located(e))?;
                self.control_emit(
                    region,
                    statement.span,
                    ctl::ControlInstruction::Loop { body },
                );
                Ok(())
            }
            S::Break(expression) => {
                if self.loops.is_empty() {
                    return Err(self.located(Report::new(
                        "break is only allowed inside loop or for",
                        statement.span,
                    )));
                }
                if let Some(expression) = expression {
                    self.prepare_expression(expression, scope, context, region)
                        .map_err(|e| self.located(e))?;
                }
                let value = expression
                    .as_ref()
                    .map(|expression| {
                        self.executable_value(expression, scope, context)
                            .and_then(|value| scalar(value, context, expression.span))
                    })
                    .transpose()
                    .map_err(|e| self.located(e))?;
                let frame = self.loops.last_mut().expect("checked loop");
                frame.types.push(value.as_ref().map(|v| v.ty.clone()));
                let inherited = frame.pending.clone();
                self.finish_lexical_region(region, &inherited, statement.span);
                self.control_emit(
                    region,
                    statement.span,
                    ctl::ControlInstruction::Break { value },
                );
                Ok(())
            }
            _ => unreachable!("control statement dispatch"),
        }
    }

    fn commit_lexical_updates(outer: &mut Scope, inner: &Scope, body: &[language::Statement]) {
        let mut names = BTreeSet::new();
        assigned_names(body, &mut names);
        for name in names {
            if !outer.values.contains_key(&name) {
                continue;
            }
            let Some(value) = inner.values.get(&name) else {
                continue;
            };
            if !matches!(
                value,
                Value::Bool(_)
                    | Value::Literal(_)
                    | Value::Dynamic(ctl::Expr {
                        ty: ctl::Type::Bool | ctl::Type::String,
                        ..
                    })
            ) {
                continue;
            }
            Arc::make_mut(&mut outer.values).insert(name.clone(), value.clone());
            if let Some(slot) = inner.cells.get(&name) {
                outer.cells.insert(name, *slot);
            }
        }
    }

    fn finish_lexical_region(
        &mut self,
        region: &mut Region,
        inherited: &BTreeSet<ProcessId>,
        span: Span,
    ) {
        let pending = self
            .pending
            .difference(inherited)
            .copied()
            .collect::<Vec<_>>();
        for process in pending {
            self.join(region, span, Some(process));
        }
    }

    fn loop_region(
        &mut self,
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
        result: bool,
    ) -> Result<(Region, Option<ctl::Type>), Failure> {
        let inherited_pending = self.pending.clone();
        let inherited_consumed = self.session.consumed.clone();
        self.loops.push(LoopFrame {
            types: Vec::new(),
            pending: inherited_pending.clone(),
        });
        let mut region = Region::default();
        let errors = self.block(body, scope, context, &mut region);
        let breaks = self.loops.pop().expect("active loop").types;
        self.finish_lexical_region(
            &mut region,
            &inherited_pending,
            body.last().map_or(Span::default(), |s| s.span),
        );
        self.pending = inherited_pending;
        let mut processes = HashSet::new();
        local_processes(&region, &mut processes);
        for ((process, port), site) in &self.session.consumed {
            if !inherited_consumed.contains_key(&(*process, *port)) && !processes.contains(process)
            {
                return Err(Failure::located(LocatedReport::new(site.source.clone(), Report::new("a loop cannot repeatedly consume a stream from an outer program", site.span).help("Launch and connect the program inside the loop, or capture its stream into a replayable file before iterating."))));
            }
        }
        if let Some(error) = errors.into_iter().next() {
            return Err(Failure::located(error));
        }
        let ty = breaks.first().cloned().flatten();
        if breaks.iter().any(|other| *other != ty) {
            return Err(Report::new(
                "all breaks in one loop must return the same type",
                body.last().map_or(Span::default(), |s| s.span),
            )
            .into());
        }
        if result && (breaks.is_empty() || ty.is_none()) {
            return Err(Report::new(
                "a loop expression must contain a break returning a value",
                body.last().map_or(Span::default(), |s| s.span),
            )
            .into());
        }
        Ok((region, ty))
    }

    fn executable_value(
        &mut self,
        expression: &Expr,
        scope: &Scope,
        context: &CompileContext,
    ) -> Result<Value, Failure> {
        if let ExprKind::Loop { body } = &expression.kind {
            let (body, ty) = self.loop_region(body, &mut scope.clone(), context, true)?;
            return Ok(Value::Dynamic(ctl::Expr {
                ty: ty.expect("value loop"),
                kind: ctl::ExprKind::Loop { body },
            }));
        }
        resolve(expression, scope, context)
    }

    pub(super) fn spawn_call(
        &mut self,
        callee: &Expr,
        arguments: &[Expr],
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
        span: Span,
    ) -> Result<Value, Failure> {
        self.prepare_expression(callee, scope, context, region)?;
        let executable = scalar(
            self.executable_value(callee, scope, context)?,
            context,
            callee.span,
        )?;
        if executable.ty != ctl::Type::Path {
            return Err(Report::new("an executable must be a path", callee.span).into());
        }
        if let ctl::ExprKind::Path { location, .. } = &executable.kind
            && !matches!(
                location.backend,
                Backend::Local | Backend::Path | Backend::Wsl | Backend::Ssh { .. }
            )
        {
            return Err(
                Report::new("this endpoint cannot launch a program", callee.span)
                    .help("Use a local, PATH, WSL or SSH executable endpoint.")
                    .into(),
            );
        }
        let mut argv = Vec::new();
        for argument in arguments {
            self.prepare_expression(argument, scope, context, region)?;
            let value = scalar(
                self.executable_value(argument, scope, context)?,
                context,
                argument.span,
            )?;
            if !matches!(value.ty, ctl::Type::String | ctl::Type::Path) {
                return Err(Report::new(
                    "program arguments must be text or file paths",
                    argument.span,
                )
                .into());
            }
            if let ctl::ExprKind::String(text) = &value.kind
                && text.contains('\0')
            {
                return Err(Report::new("program arguments cannot contain NUL", argument.span).help("Pass these bytes through stdin or a file instead; native argv strings cannot contain NUL.").into());
            }
            argv.push(value);
        }
        let dynamic = !matches!(executable.kind, ctl::ExprKind::Path { .. })
            || argv.iter().any(|arg| {
                !matches!(
                    arg.kind,
                    ctl::ExprKind::String(_) | ctl::ExprKind::Path { .. }
                )
            });
        let process = ProcessId(self.session.next_process);
        self.session.next_process += 1;
        if dynamic {
            let cwd = ctl::Expr {
                ty: ctl::Type::Path,
                kind: ctl::ExprKind::Path {
                    location: Location {
                        backend: Backend::Local,
                        path: context.pwd.to_string_lossy().into_owned(),
                    },
                    mode: DestinationMode::Exact,
                    relocation: Some(RelocPath::new(Anchor::WorkingDirectory, "")),
                },
            };
            self.control_emit(
                region,
                span,
                ctl::ControlInstruction::Spawn {
                    process,
                    executable,
                    arguments: argv,
                    cwd,
                },
            );
        } else {
            let ctl::ExprKind::Path {
                location: executable,
                relocation,
                ..
            } = executable.kind
            else {
                unreachable!("static executable");
            };
            let mut relocations = Vec::new();
            if let Some(path) = relocation {
                relocations.push((Slot::Executable, path));
            }
            let arguments = argv
                .into_iter()
                .enumerate()
                .map(|(index, arg)| match arg.kind {
                    ctl::ExprKind::String(text) => Argument::Text(text),
                    ctl::ExprKind::Path {
                        location,
                        relocation,
                        ..
                    } => {
                        if let Some(path) = relocation {
                            relocations.push((Slot::Argument { index }, path));
                        }
                        Argument::Path(location)
                    }
                    _ => unreachable!("static program argument"),
                })
                .collect();
            let instruction = self.emit(
                region,
                span,
                InstructionKind::Spawn {
                    process,
                    executable,
                    arguments,
                    cwd: context.pwd.clone(),
                },
            );
            relocations.push((Slot::Cwd, RelocPath::new(Anchor::WorkingDirectory, "")));
            self.relocations
                .extend(relocations.into_iter().map(|(slot, path)| Relocation {
                    instruction,
                    slot,
                    path,
                }));
        }
        self.pending.insert(process);
        Ok(Value::Process(process))
    }

    pub(super) fn bind_control(
        &mut self,
        name: &Name,
        op: BindingOp,
        expression: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<bool, Failure> {
        // Program/space bindings retain their established static lowering.
        if op == BindingOp::Space
            && matches!(expression.kind, ExprKind::Call { .. })
            && !symbols::is_call(expression, scope)
        {
            return Ok(false);
        }
        self.prepare_expression(expression, scope, context, region)?;
        let value = self.executable_value(expression, scope, context)?;
        check_assignment(op, &value, expression.span)?;
        if op == BindingOp::Assign {
            check_reassignment(name, &value, scope, context)?;
            if let Some(slot) = scope.cells.get(&name.text).copied() {
                let value = scalar(value, context, expression.span)?;
                let ty = value.ty.clone();
                self.control_emit(
                    region,
                    expression.span,
                    ctl::ControlInstruction::Set { slot, value },
                );
                self.record_value(
                    name,
                    expression,
                    Value::Dynamic(ctl::Expr {
                        ty,
                        kind: ctl::ExprKind::Slot(slot),
                    }),
                    scope,
                );
                return Ok(true);
            }
        } else {
            scope.cells.remove(&name.text);
        }
        if !matches!(value, Value::Dynamic(_) | Value::Range(_)) {
            if op == BindingOp::Space && !matches!(value, Value::Space(_) | Value::Process(_)) {
                return Err(Report::new("'=>' expects a space or program", expression.span).into());
            }
            let mut value = value;
            if let Value::Path(path) = &mut value
                && contextual(path.relocation.as_ref())
                && path.relocation_unit.is_none()
            {
                path.relocation_unit = scope.current_unit;
            }
            self.record_value(name, expression, value, scope);
            return Ok(true);
        }
        if let Value::Range(_) = value {
            if op != BindingOp::Value {
                return Err(Report::new("a range is bound with ':='", expression.span).into());
            }
            self.record_value(name, expression, value, scope);
            return Ok(true);
        }
        let value = scalar(value, context, expression.span)?;
        if op == BindingOp::Space && value.ty != ctl::Type::Space {
            return Err(Report::new("'=>' expects a space or program", expression.span).into());
        }
        let slot = scope.slot();
        let ty = value.ty.clone();
        if op == BindingOp::Assign {
            scope.cells.insert(name.text.clone(), slot);
        }
        self.control_emit(
            region,
            Span {
                start: name.span.start,
                end: expression.span.end,
            },
            ctl::ControlInstruction::Let { slot, value },
        );
        self.record_value(
            name,
            expression,
            Value::Dynamic(ctl::Expr {
                ty,
                kind: ctl::ExprKind::Slot(slot),
            }),
            scope,
        );
        Ok(true)
    }

    fn record_value(&self, name: &Name, expression: &Expr, value: Value, scope: &mut Scope) {
        let trace = ExpansionTrace::new(
            &name.text,
            &value,
            self.source.at(name.span),
            ExpansionTrace::inputs(expression, scope),
            ExpansionOrigin::Item,
        );
        Arc::make_mut(&mut scope.expansions).insert(name.text.clone(), trace);
        let site = self.expression_site(expression, scope);
        Arc::make_mut(&mut scope.sites).insert(name.text.clone(), site);
        if let Some(unit) = scope.current_unit {
            Arc::make_mut(&mut scope.defined_units).insert(name.text.clone(), unit);
        }
        Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
    }

    pub(super) fn transfer_control(
        &mut self,
        source: &Expr,
        destination: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        span: Span,
        region: &mut Region,
    ) -> Result<Option<(Value, Value)>, Failure> {
        self.prepare_expression(source, scope, context, region)?;
        self.prepare_expression(destination, scope, context, region)?;
        let source_value = self.executable_value(source, scope, context)?;
        let destination_value = self.executable_value(destination, scope, context)?;
        if !matches!(source_value, Value::Dynamic(_) | Value::Range(_))
            && !matches!(destination_value, Value::Dynamic(_))
        {
            return Ok(Some((source_value, destination_value)));
        }
        let mode = match &destination_value {
            Value::Path(path) if expression_mode(destination) == DestinationMode::Exact => {
                path.mode
            }
            _ => expression_mode(destination),
        };
        let destination_value = match destination_value {
            Value::Port(process, Port::Stdin) => {
                self.claim_port((process, Port::Stdin), destination.span)?;
                ctl::TransferOutput::Stdin(process)
            }
            value => {
                let value = match value {
                    Value::Path(mut path) => {
                        if matches!(path.backend, Some(Backend::Http { .. } | Backend::Path)) {
                            return Err(Report::new(
                                "HTTP(S) and PATH endpoints are read-only",
                                destination.span,
                            )
                            .into());
                        }
                        if path.path.contains(['<', '>']) {
                            return Err(Report::new(
                                "selectors are only allowed in the source",
                                destination.span,
                            )
                            .into());
                        }
                        if mode == DestinationMode::ReplaceTree {
                            if path.path.ends_with("/.") || path.path.ends_with("\\.") {
                                path.path.pop();
                                if let Some(relocation) = &mut path.relocation {
                                    relocation.suffix.pop();
                                }
                            }
                            reject_root_replacement(&path, destination.span)?;
                        } else if mode == DestinationMode::Exact
                            && !matches!(path.backend, Some(Backend::Tcp { .. }))
                        {
                            reject_root_replacement(&path, destination.span)?;
                        }
                        Value::Path(path)
                    }
                    value => value,
                };
                let path = scalar(value, context, destination.span)?;
                if path.ty != ctl::Type::Path {
                    return Err(Report::new(
                        "transfer destination must be a writable path",
                        destination.span,
                    )
                    .into());
                }
                if let ctl::ExprKind::Path { location, .. } = &path.kind
                    && matches!(location.backend, Backend::Http { .. } | Backend::Path)
                {
                    return Err(Report::new(
                        "HTTP(S) and PATH endpoints are read-only",
                        destination.span,
                    )
                    .into());
                }
                ctl::TransferOutput::File { path, mode }
            }
        };
        if let Value::Range(range) = source_value {
            if mode != DestinationMode::Container
                || !matches!(destination_value, ctl::TransferOutput::File { .. })
            {
                return Err(Report::new(
                    "a range transfer requires a destination container ending in '/'",
                    destination.span,
                )
                .into());
            }
            let slot = scope.slot();
            let source = ctl::TransferInput::Scalar(ctl::Expr {
                ty: range.item_type.clone(),
                kind: ctl::ExprKind::Slot(slot),
            });
            let mut body = Region::default();
            self.control_emit(
                &mut body,
                span,
                ctl::ControlInstruction::Transfer {
                    source,
                    destination: destination_value,
                },
            );
            self.control_emit(
                region,
                span,
                ctl::ControlInstruction::For { slot, range, body },
            );
            return Ok(None);
        }
        let source_value = match source_value {
            Value::Port(process, Port::Read(port)) => {
                if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
                    self.claim_port((process, Port::Read(port)), source.span)?;
                }
                ctl::TransferInput::Port { process, port }
            }
            value => {
                let value = scalar(value, context, source.span)?;
                require_path_or_string(&value, source.span)?;
                ctl::TransferInput::Scalar(value)
            }
        };
        let id = self.emit(
            region,
            span,
            InstructionKind::Control(ctl::ControlInstruction::Transfer {
                source: source_value,
                destination: destination_value,
            }),
        );
        let operands = (
            self.expression_site(source, scope),
            self.expression_site(destination, scope),
        );
        self.origins
            .get_mut(&id)
            .expect("emitted control origin")
            .operands = Some(operands);
        Ok(None)
    }

    fn claim_port(&mut self, key: (ProcessId, Port), span: Span) -> Result<(), Failure> {
        if let Some(previous) = self.session.consumed.get(&key) {
            return Err(Failure::located(self.located(Report::new("a live program stream may have only one owner", span).help("Write the stream to a file once, then read that replayable file from multiple consumers.")).label(previous.clone(), "this stream was already connected here")));
        }
        self.session.consumed.insert(key, self.source.at(span));
        Ok(())
    }
}
