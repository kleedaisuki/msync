use super::*;
use crate::hir::control::{
    BinaryOp, ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource,
    RangeStage, SlotId, TransferInput, TransferOutput, Type,
};

pub(super) enum LoopResult {
    Fixed(Option<Type>),
    Infer(Option<Option<Type>>),
}

impl ProgramState {
    pub(super) fn control(&mut self, control: &Control) -> Result<(), ObjectError> {
        match control {
            Control::Spawn {
                process,
                executable,
                arguments,
                cwd,
            } => {
                self.expect(executable, Type::Path)?;
                self.expect(cwd, Type::Path)?;
                if let ExprKind::Path { location, .. } = &cwd.kind
                    && location.backend != Backend::Local
                {
                    return Err(ObjectError::new(
                        "object program cwd must use the local filesystem",
                    ));
                }
                if let ExprKind::Path { location, .. } = &executable.kind
                    && !matches!(
                        location.backend,
                        Backend::Local
                            | Backend::Path
                            | Backend::Env
                            | Backend::Wsl
                            | Backend::Ssh { .. }
                    )
                {
                    return Err(ObjectError::new(
                        "object program executable has an invalid endpoint type",
                    ));
                }
                for argument in arguments {
                    path_type(&self.expression(argument)?)?;
                    if let ExprKind::String(text) = &argument.kind {
                        no_nul(text, "program argument")?;
                    }
                }
                if process.0 > u32::MAX as usize || !self.process_definitions.insert(*process) {
                    return Err(ObjectError::new(
                        "duplicate or out-of-range object process identity",
                    ));
                }
                self.processes.insert(*process);
            }
            Control::Let { slot, value } => {
                let ty = self.expression(value)?;
                self.define_slot(*slot, ty)?;
            }
            Control::Set { slot, value } => {
                let ty = self.expression(value)?;
                if !matches!(ty, Type::Bool | Type::String) || self.slots.get(slot) != Some(&ty) {
                    return Err(ObjectError::new(
                        "object reassignment requires an existing same-typed bool or string slot",
                    ));
                }
            }
            Control::If {
                condition,
                then_body,
                else_body,
            } => {
                self.expect(condition, Type::Bool)?;
                let slots = self.slots.clone();
                let consumed = self.consumed.clone();
                let processes = self.processes.clone();
                self.region(then_body)?;
                let then_consumed = self.consumed.clone();
                let then_processes = self.processes.clone();
                self.slots = slots.clone();
                self.consumed = consumed;
                self.processes = processes;
                self.region(else_body)?;
                self.consumed.extend(then_consumed);
                self.processes = self
                    .processes
                    .intersection(&then_processes)
                    .copied()
                    .collect();
                self.slots = slots;
            }
            Control::For { slot, range, body } => {
                let ty = self.range(range)?;
                let slots = self.slots.clone();
                self.define_slot(*slot, ty)?;
                self.loop_body(body, LoopResult::Fixed(None))?;
                self.slots = slots;
            }
            Control::Loop { body } => {
                let slots = self.slots.clone();
                self.loop_body(body, LoopResult::Infer(None))?;
                self.slots = slots;
            }
            Control::Break { value } => {
                let ty = value
                    .as_ref()
                    .map(|value| self.expression(value))
                    .transpose()?;
                let expected = self
                    .loops
                    .last_mut()
                    .ok_or_else(|| ObjectError::new("object break is outside a loop"))?;
                match expected {
                    LoopResult::Infer(value @ None) => *value = Some(ty),
                    LoopResult::Infer(Some(value)) | LoopResult::Fixed(value) if value != &ty => {
                        return Err(ObjectError::new(
                            "object break result does not match its loop result type",
                        ));
                    }
                    _ => {}
                }
            }
            Control::Delete { path } => {
                let ty = self.expression(path)?;
                path_type(&ty)?;
            }
            Control::Transfer {
                source,
                destination,
                ..
            } => {
                match source {
                    TransferInput::Scalar(value) => path_type(&self.expression(value)?)?,
                    TransferInput::Port {
                        process,
                        port: ReadPort::Stdout,
                    } => self.consume(*process, 1)?,
                    TransferInput::Port {
                        process,
                        port: ReadPort::Stderr,
                    } => self.consume(*process, 2)?,
                    TransferInput::Port { process, .. } => self.process(*process)?,
                }
                match destination {
                    TransferOutput::File { path, .. } => {
                        self.expect(path, Type::Path)?;
                        if let ExprKind::Path { location, .. } = &path.kind
                            && matches!(location.backend, Backend::Http { .. } | Backend::Path)
                        {
                            return Err(ObjectError::new(
                                "object destination is a readonly namespace",
                            ));
                        }
                    }
                    TransferOutput::Stdin(process) => self.consume(*process, 0)?,
                }
            }
        }
        Ok(())
    }

    fn loop_body(&mut self, body: &Region, result: LoopResult) -> Result<(), ObjectError> {
        let processes = self.processes.clone();
        let consumed = self.consumed.clone();
        self.loops.push(result);
        self.region(body)?;
        self.loops.pop();
        if self
            .consumed
            .difference(&consumed)
            .any(|(process, _)| processes.contains(process))
        {
            return Err(ObjectError::new(
                "object loop cannot repeatedly consume an outer process stream",
            ));
        }
        self.processes = processes;
        Ok(())
    }

    fn define_slot(&mut self, slot: SlotId, ty: Type) -> Result<(), ObjectError> {
        if slot.0 > u32::MAX as usize {
            return Err(ObjectError::new("object runtime slot is out of range"));
        }
        if self.slots.insert(slot, ty).is_some() {
            return Err(ObjectError::new(
                "object runtime slot has duplicate definitions",
            ));
        }
        Ok(())
    }

    fn expect(&mut self, value: &Expr, ty: Type) -> Result<(), ObjectError> {
        if self.expression(value)? != ty {
            return Err(ObjectError::new(
                "object expression has an incompatible operand type",
            ));
        }
        Ok(())
    }

    pub(super) fn expression(&mut self, expression: &Expr) -> Result<Type, ObjectError> {
        self.depth += 1;
        if self.depth > 128 {
            return Err(ObjectError::new(
                "object expression nesting exceeds 128 levels",
            ));
        }
        let ty = match &expression.kind {
            ExprKind::Bool(_) => Type::Bool,
            ExprKind::String(_) => Type::String,
            ExprKind::Path {
                location,
                relocation,
                ..
            } => {
                validate_backend(&location.backend)?;
                no_nul(&location.path, "control path")?;
                if let Some(path) = relocation {
                    if !location.path.is_empty() {
                        return Err(ObjectError::new(
                            "relocated control operand contains a stale physical binding",
                        ));
                    }
                    path_expression(path)?;
                    anchor_backend(&path.anchor, &location.backend)?;
                } else {
                    operand(InstructionId(0), Slot::Source, location, &BTreeSet::new())?;
                }
                Type::Path
            }
            ExprKind::Space(namespace) => {
                if let Namespace::Endpoint { backend } = namespace {
                    validate_backend(backend)?;
                }
                Type::Space
            }
            ExprKind::ResolvedSpace {
                namespace,
                pwd,
                module_file,
            } => {
                if !pwd.is_absolute()
                    || module_file.as_ref().is_some_and(|file| !file.is_absolute())
                {
                    return Err(ObjectError::new(
                        "object resolved space context must use absolute paths",
                    ));
                }
                if let Namespace::Endpoint { backend } = namespace {
                    validate_backend(backend)?;
                }
                Type::Space
            }
            ExprKind::Slot(slot) => self.slots.get(slot).cloned().ok_or_else(|| {
                ObjectError::new("object expression references an undefined runtime slot")
            })?,
            ExprKind::Let { slot, value, body } => {
                let ty = self.expression(value)?;
                self.define_slot(*slot, ty)?;
                let result = self.expression(body)?;
                self.slots.remove(slot);
                result
            }
            ExprKind::Exists(path) => {
                path_type(&self.expression(path)?)?;
                Type::Bool
            }
            ExprKind::Not(value) => {
                self.expect(value, Type::Bool)?;
                Type::Bool
            }
            ExprKind::Binary { left, op, right } => {
                let left = self.expression(left)?;
                let right = self.expression(right)?;
                let valid = match op {
                    BinaryOp::And | BinaryOp::Or => left == Type::Bool && right == Type::Bool,
                    BinaryOp::Eq | BinaryOp::Ne => {
                        left == right && matches!(left, Type::Bool | Type::String)
                    }
                    BinaryOp::Lt | BinaryOp::Le | BinaryOp::Gt | BinaryOp::Ge => {
                        left == Type::String && right == Type::String
                    }
                };
                if !valid {
                    return Err(ObjectError::new(
                        "object binary operator has incompatible operands",
                    ));
                }
                Type::Bool
            }
            ExprKind::Join {
                base,
                suffix,
                suffix_backend,
                ..
            } => {
                if let Some(backend) = suffix_backend {
                    validate_backend(backend)?;
                }
                path_type(&self.expression(base)?)?;
                path_type(&self.expression(suffix)?)?;
                Type::Path
            }
            ExprKind::Select { namespace, item } => {
                self.expect(namespace, Type::Space)?;
                path_type(&self.expression(item)?)?;
                Type::Path
            }
            ExprKind::Loop { body } => {
                let slots = self.slots.clone();
                self.loop_body(body, LoopResult::Fixed(Some(expression.ty.clone())))?;
                self.slots = slots;
                expression.ty.clone()
            }
            ExprKind::RangeQuery { range, query } => {
                let item_type = self.range(range)?;
                match query {
                    RangeQuery::Contains { value } | RangeQuery::FindValue { value } => {
                        self.expect(value, item_type.clone())?;
                        if matches!(query, RangeQuery::Contains { .. }) {
                            Type::Bool
                        } else {
                            item_type
                        }
                    }
                    RangeQuery::Empty => Type::Bool,
                    RangeQuery::First | RangeQuery::Last => item_type,
                    RangeQuery::Any { slot, predicate }
                    | RangeQuery::All { slot, predicate }
                    | RangeQuery::None { slot, predicate }
                    | RangeQuery::Find { slot, predicate } => {
                        self.define_slot(*slot, item_type.clone())?;
                        self.expect(predicate, Type::Bool)?;
                        self.slots.remove(slot);
                        if matches!(query, RangeQuery::Find { .. }) {
                            item_type
                        } else {
                            Type::Bool
                        }
                    }
                }
            }
        };
        self.depth -= 1;
        if ty != expression.ty {
            return Err(ObjectError::new(
                "object expression result type disagrees with its annotation",
            ));
        }
        Ok(ty)
    }

    pub(super) fn range(&mut self, range: &RangeExpr) -> Result<Type, ObjectError> {
        if range.stages.len() > 128 {
            return Err(ObjectError::new("object range pipeline exceeds 128 stages"));
        }
        let mut item_type = match &range.source {
            RangeSource::Regex { root, pattern } => {
                path_type(&self.expression(root)?)?;
                regex::Regex::new(pattern).map_err(|error| {
                    ObjectError::new(format!("invalid object range selector: {error}"))
                })?;
                Type::Path
            }
            RangeSource::Values(values) => {
                let ty = values
                    .first()
                    .map(|value| self.expression(value))
                    .transpose()?
                    .unwrap_or(range.item_type.clone());
                path_type(&ty)?;
                for value in values.iter().skip(1) {
                    self.expect(value, ty.clone())?;
                }
                ty
            }
        };
        for stage in &range.stages {
            match stage {
                RangeStage::Filter { slot, predicate }
                | RangeStage::TakeWhile { slot, predicate }
                | RangeStage::DropWhile { slot, predicate } => {
                    self.define_slot(*slot, item_type.clone())?;
                    self.expect(predicate, Type::Bool)?;
                    self.slots.remove(slot);
                }
                RangeStage::Transform { slot, expression } => {
                    self.define_slot(*slot, item_type)?;
                    item_type = self.expression(expression)?;
                    path_type(&item_type)?;
                    self.slots.remove(slot);
                }
                RangeStage::Stride(0) => {
                    return Err(ObjectError::new("object range stride must be positive"));
                }
                _ => {}
            }
        }
        if item_type != range.item_type {
            return Err(ObjectError::new(
                "object range item type disagrees with its stages",
            ));
        }
        Ok(item_type)
    }
}

fn path_type(ty: &Type) -> Result<(), ObjectError> {
    if !matches!(ty, Type::String | Type::Path) {
        return Err(ObjectError::new(
            "object operation requires a path or string operand",
        ));
    }
    Ok(())
}
