//! Recursive typed-IR traversal shared by object validation and instantiation.

use crate::hir::control::{
    ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource, RangeStage,
    TransferInput, TransferOutput,
};
use crate::hir::{Instruction, InstructionKind, Region};

pub(crate) trait Visitor {
    type Error;
    fn instruction(&mut self, _node: &mut Instruction) -> Result<(), Self::Error> {
        Ok(())
    }
    fn leave_instruction(&mut self, _node: &mut Instruction) -> Result<(), Self::Error> {
        Ok(())
    }
    fn expression(&mut self, _expression: &mut Expr) -> Result<(), Self::Error> {
        Ok(())
    }
    fn range(&mut self, _range: &mut RangeExpr) -> Result<(), Self::Error> {
        Ok(())
    }
    fn control(&mut self, _control: &mut Control) -> Result<(), Self::Error> {
        Ok(())
    }
}

pub(crate) fn visit_region<V: Visitor>(
    region: &mut Region,
    visitor: &mut V,
) -> Result<(), V::Error> {
    for node in &mut region.instructions {
        visitor.instruction(node)?;
        if let InstructionKind::Control(control) = &mut node.kind {
            visit_control(control, visitor)?;
        }
        visitor.leave_instruction(node)?;
    }
    Ok(())
}

pub(crate) fn visit_control<V: Visitor>(
    control: &mut Control,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.control(control)?;
    match control {
        Control::Spawn {
            executable,
            arguments,
            cwd,
            ..
        } => {
            visit_expr(executable, visitor)?;
            for argument in arguments {
                visit_expr(argument, visitor)?;
            }
            visit_expr(cwd, visitor)?;
        }
        Control::Let { value, .. } | Control::Set { value, .. } => visit_expr(value, visitor)?,
        Control::If {
            condition,
            then_body,
            else_body,
        } => {
            visit_expr(condition, visitor)?;
            visit_region(then_body, visitor)?;
            visit_region(else_body, visitor)?;
        }
        Control::For { range, body, .. } => {
            visit_range(range, visitor)?;
            visit_region(body, visitor)?;
        }
        Control::Loop { body } => visit_region(body, visitor)?,
        Control::Break { value } => {
            if let Some(value) = value {
                visit_expr(value, visitor)?;
            }
        }
        Control::Delete { path } => visit_expr(path, visitor)?,
        Control::Transfer {
            source,
            destination,
            ..
        } => {
            if let TransferInput::Scalar(value) = source {
                visit_expr(value, visitor)?;
            }
            if let TransferOutput::File { path, .. } = destination {
                visit_expr(path, visitor)?;
            }
        }
    }
    Ok(())
}

pub(crate) fn visit_expr<V: Visitor>(
    expression: &mut Expr,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.expression(expression)?;
    match &mut expression.kind {
        ExprKind::Exists(value) | ExprKind::Not(value) => visit_expr(value, visitor)?,
        ExprKind::Let { value, body, .. } => {
            visit_expr(value, visitor)?;
            visit_expr(body, visitor)?;
        }
        ExprKind::Binary { left, right, .. } => {
            visit_expr(left, visitor)?;
            visit_expr(right, visitor)?;
        }
        ExprKind::Join { base, suffix, .. } => {
            visit_expr(base, visitor)?;
            visit_expr(suffix, visitor)?;
        }
        ExprKind::Select { namespace, item } => {
            visit_expr(namespace, visitor)?;
            visit_expr(item, visitor)?;
        }
        ExprKind::Loop { body } => visit_region(body, visitor)?,
        ExprKind::RangeQuery { range, query } => {
            visit_range(range, visitor)?;
            match query {
                RangeQuery::Contains { value } | RangeQuery::FindValue { value } => {
                    visit_expr(value, visitor)?
                }
                RangeQuery::Any { predicate, .. }
                | RangeQuery::All { predicate, .. }
                | RangeQuery::None { predicate, .. }
                | RangeQuery::Find { predicate, .. } => visit_expr(predicate, visitor)?,
                RangeQuery::Empty | RangeQuery::First | RangeQuery::Last => {}
            }
        }
        ExprKind::Bool(_)
        | ExprKind::String(_)
        | ExprKind::Path { .. }
        | ExprKind::Space(_)
        | ExprKind::ResolvedSpace { .. }
        | ExprKind::Slot(_) => {}
    }
    Ok(())
}

pub(crate) fn visit_range<V: Visitor>(
    range: &mut RangeExpr,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.range(range)?;
    match &mut range.source {
        RangeSource::Regex { root, .. } => visit_expr(root, visitor)?,
        RangeSource::Values(values) => {
            for value in values {
                visit_expr(value, visitor)?;
            }
        }
    }
    for stage in &mut range.stages {
        match stage {
            RangeStage::Filter { predicate, .. }
            | RangeStage::TakeWhile { predicate, .. }
            | RangeStage::DropWhile { predicate, .. } => visit_expr(predicate, visitor)?,
            RangeStage::Transform { expression, .. } => visit_expr(expression, visitor)?,
            RangeStage::Take(_)
            | RangeStage::Drop(_)
            | RangeStage::Reverse
            | RangeStage::Stride(_)
            | RangeStage::Unique => {}
        }
    }
    Ok(())
}

pub(crate) use crate::hir::walk::{
    ReadVisitor, append_instructions as region_nodes, read_control, read_expr, read_range,
    read_region,
};

pub(crate) struct InlinePaths<'ir> {
    pub paths: Vec<(
        &'ir crate::endpoint::Location,
        Option<&'ir crate::relocatable::Path>,
    )>,
}
impl<'ir> ReadVisitor<'ir> for InlinePaths<'ir> {
    type Error = super::ObjectError;
    fn expression(&mut self, expression: &'ir Expr) -> Result<(), Self::Error> {
        if let ExprKind::Path {
            location,
            relocation,
            ..
        } = &expression.kind
        {
            self.paths.push((location, relocation.as_ref()));
        }
        Ok(())
    }
}

pub(crate) struct ClearInlinePaths;
impl Visitor for ClearInlinePaths {
    type Error = super::ObjectError;
    fn expression(&mut self, expression: &mut Expr) -> Result<(), Self::Error> {
        if let ExprKind::Path {
            location,
            relocation: Some(_),
            ..
        } = &mut expression.kind
        {
            location.path.clear();
        }
        Ok(())
    }
}
