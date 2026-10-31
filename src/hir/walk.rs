//! Borrowed recursive typed-IR inspection, including expression-owned regions.

use crate::hir::control::{
    ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource, RangeStage,
    TransferInput, TransferOutput,
};
use crate::hir::{Instruction, InstructionKind, Region};

/// Return instructions in lexical preorder, including control and expression bodies.
pub fn instructions(region: &Region) -> Vec<&Instruction> {
    let mut nodes = Vec::new();
    append_instructions(region, &mut nodes);
    nodes
}

pub(crate) trait ReadVisitor<'ir> {
    type Error;
    fn instruction(&mut self, _node: &'ir Instruction) -> Result<(), Self::Error> {
        Ok(())
    }
    fn leave_instruction(&mut self, _node: &'ir Instruction) -> Result<(), Self::Error> {
        Ok(())
    }
    fn expression(&mut self, _expression: &'ir Expr) -> Result<(), Self::Error> {
        Ok(())
    }
    fn range(&mut self, _range: &'ir RangeExpr) -> Result<(), Self::Error> {
        Ok(())
    }
    fn control(&mut self, _control: &'ir Control) -> Result<(), Self::Error> {
        Ok(())
    }
}

pub(crate) fn read_region<'ir, V: ReadVisitor<'ir>>(
    region: &'ir Region,
    visitor: &mut V,
) -> Result<(), V::Error> {
    for node in &region.instructions {
        visitor.instruction(node)?;
        if let InstructionKind::Control(control) = &node.kind {
            read_control(control, visitor)?;
        }
        visitor.leave_instruction(node)?;
    }
    Ok(())
}

pub(crate) fn read_control<'ir, V: ReadVisitor<'ir>>(
    control: &'ir Control,
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
            read_expr(executable, visitor)?;
            for argument in arguments {
                read_expr(argument, visitor)?;
            }
            read_expr(cwd, visitor)?;
        }
        Control::Let { value, .. } | Control::Set { value, .. } => read_expr(value, visitor)?,
        Control::If {
            condition,
            then_body,
            else_body,
        } => {
            read_expr(condition, visitor)?;
            read_region(then_body, visitor)?;
            read_region(else_body, visitor)?;
        }
        Control::For { range, body, .. } => {
            read_range(range, visitor)?;
            read_region(body, visitor)?;
        }
        Control::Loop { body } => read_region(body, visitor)?,
        Control::Break { value } => {
            if let Some(value) = value {
                read_expr(value, visitor)?;
            }
        }
        Control::Delete { path } => read_expr(path, visitor)?,
        Control::Transfer {
            source,
            destination,
            ..
        } => {
            if let TransferInput::Scalar(value) = source {
                read_expr(value, visitor)?;
            }
            if let TransferOutput::File { path, .. } = destination {
                read_expr(path, visitor)?;
            }
        }
    }
    Ok(())
}

pub(crate) fn read_expr<'ir, V: ReadVisitor<'ir>>(
    expression: &'ir Expr,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.expression(expression)?;
    match &expression.kind {
        ExprKind::Exists(value) | ExprKind::Not(value) => read_expr(value, visitor)?,
        ExprKind::Let { value, body, .. } => {
            read_expr(value, visitor)?;
            read_expr(body, visitor)?;
        }
        ExprKind::Binary { left, right, .. } => {
            read_expr(left, visitor)?;
            read_expr(right, visitor)?;
        }
        ExprKind::Join { base, suffix, .. } => {
            read_expr(base, visitor)?;
            read_expr(suffix, visitor)?;
        }
        ExprKind::Select { namespace, item } => {
            read_expr(namespace, visitor)?;
            read_expr(item, visitor)?;
        }
        ExprKind::Loop { body } => read_region(body, visitor)?,
        ExprKind::RangeQuery { range, query } => {
            read_range(range, visitor)?;
            match query {
                RangeQuery::Contains { value } | RangeQuery::FindValue { value } => {
                    read_expr(value, visitor)?
                }
                RangeQuery::Any { predicate, .. }
                | RangeQuery::All { predicate, .. }
                | RangeQuery::None { predicate, .. }
                | RangeQuery::Find { predicate, .. } => read_expr(predicate, visitor)?,
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

pub(crate) fn read_range<'ir, V: ReadVisitor<'ir>>(
    range: &'ir RangeExpr,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.range(range)?;
    match &range.source {
        RangeSource::Regex { root, .. } => read_expr(root, visitor)?,
        RangeSource::Values(values) => {
            for value in values {
                read_expr(value, visitor)?;
            }
        }
    }
    for stage in &range.stages {
        match stage {
            RangeStage::Filter { predicate, .. }
            | RangeStage::TakeWhile { predicate, .. }
            | RangeStage::DropWhile { predicate, .. } => read_expr(predicate, visitor)?,
            RangeStage::Transform { expression, .. } => read_expr(expression, visitor)?,
            RangeStage::Take(_)
            | RangeStage::Drop(_)
            | RangeStage::Reverse
            | RangeStage::Stride(_)
            | RangeStage::Unique => {}
        }
    }
    Ok(())
}

pub(crate) fn append_instructions<'ir>(region: &'ir Region, nodes: &mut Vec<&'ir Instruction>) {
    struct Nodes<'out, 'ir>(&'out mut Vec<&'ir Instruction>);
    impl<'ir> ReadVisitor<'ir> for Nodes<'_, 'ir> {
        type Error = std::convert::Infallible;
        fn instruction(&mut self, node: &'ir Instruction) -> Result<(), Self::Error> {
            self.0.push(node);
            Ok(())
        }
    }
    read_region(region, &mut Nodes(nodes)).unwrap();
}
