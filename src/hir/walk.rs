//! Borrowed recursive typed-IR inspection, including expression-owned regions.

use crate::endpoint::Backend;
use crate::hir::control::{
    ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource, RangeStage,
    TransferInput, TransferOutput,
};
use crate::hir::{Argument, Input, Instruction, InstructionKind, Output, Region};
use crate::relocatable::Namespace;

/// Return instructions in lexical preorder, including control and expression bodies.
pub fn instructions(region: &Region) -> Vec<&Instruction> {
    let mut nodes = Vec::new();
    append_instructions(region, &mut nodes);
    nodes
}

/// Referenced endpoint identities in lexical order, without observing their state.
pub fn backends(region: &Region) -> Vec<&Backend> {
    #[derive(Default)]
    struct Backends<'ir> {
        seen: std::collections::HashSet<&'ir Backend>,
        values: Vec<&'ir Backend>,
    }
    impl<'ir> Backends<'ir> {
        fn add(&mut self, backend: &'ir Backend) {
            if self.seen.insert(backend) {
                self.values.push(backend);
            }
        }
        fn namespace(&mut self, namespace: &'ir Namespace) {
            match namespace {
                Namespace::Endpoint { backend } => self.add(backend),
                Namespace::Qualified { outer, inner } => {
                    self.namespace(outer);
                    self.namespace(inner);
                }
                Namespace::WorkingDirectory
                | Namespace::Module
                | Namespace::Api { .. }
                | Namespace::Lexical { .. } => {}
            }
        }
    }
    impl<'ir> ReadVisitor<'ir> for Backends<'ir> {
        type Error = std::convert::Infallible;
        fn instruction(&mut self, node: &'ir Instruction) -> Result<(), Self::Error> {
            match &node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    if let Input::File { location, .. } = source {
                        self.add(&location.backend);
                    }
                    if let Output::File { location, .. } = destination {
                        self.add(&location.backend);
                    }
                }
                InstructionKind::Spawn {
                    executable,
                    arguments,
                    ..
                } => {
                    self.add(&executable.backend);
                    for argument in arguments {
                        if let Argument::Path(location) = argument {
                            self.add(&location.backend);
                        }
                    }
                }
                _ => {}
            }
            Ok(())
        }
        fn expression(&mut self, expression: &'ir Expr) -> Result<(), Self::Error> {
            match &expression.kind {
                ExprKind::Path { location, .. } => self.add(&location.backend),
                ExprKind::Space(namespace) | ExprKind::ResolvedSpace { namespace, .. } => {
                    self.namespace(namespace)
                }
                ExprKind::Join {
                    suffix_backend: Some(backend),
                    ..
                } => self.add(backend),
                _ => {}
            }
            Ok(())
        }
    }
    let mut visitor = Backends::default();
    read_region(region, &mut visitor).unwrap();
    visitor.values
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
        Control::Native { arguments, .. } => {
            for argument in arguments {
                read_expr(argument, visitor)?;
            }
        }
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
        Control::RangeTransfer {
            range, destination, ..
        } => {
            read_range(range, visitor)?;
            read_expr(destination, visitor)?;
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
        ExprKind::Native { arguments, .. } => {
            for argument in arguments {
                read_expr(argument, visitor)?;
            }
        }
        ExprKind::UnboundPath { local, .. } => read_expr(local, visitor)?,
        ExprKind::Space(namespace) | ExprKind::ResolvedSpace { namespace, .. } => {
            read_namespace(namespace, visitor)?;
        }
        ExprKind::Exists(value) | ExprKind::Not(value) | ExprKind::IntoPath { value, .. } => {
            read_expr(value, visitor)?
        }
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
        ExprKind::Bool(_) | ExprKind::String(_) | ExprKind::Path { .. } | ExprKind::Slot(_) => {}
    }
    Ok(())
}

fn read_namespace<'ir, V: ReadVisitor<'ir>>(
    namespace: &'ir Namespace,
    visitor: &mut V,
) -> Result<(), V::Error> {
    match namespace {
        Namespace::Lexical { members } => {
            for value in members.values() {
                read_expr(value, visitor)?;
            }
        }
        Namespace::Qualified { outer, inner } => {
            read_namespace(outer, visitor)?;
            read_namespace(inner, visitor)?;
        }
        _ => {}
    }
    Ok(())
}

pub(crate) fn read_range<'ir, V: ReadVisitor<'ir>>(
    range: &'ir RangeExpr,
    visitor: &mut V,
) -> Result<(), V::Error> {
    visitor.range(range)?;
    match &range.source {
        RangeSource::Let { bindings, range } => {
            for (_, value) in bindings {
                read_expr(value, visitor)?;
            }
            read_range(range, visitor)?;
        }
        RangeSource::Native { arguments, .. } => {
            for argument in arguments {
                read_expr(argument, visitor)?;
            }
        }
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hir::control::Type;
    use crate::language::DestinationMode;

    #[test]
    fn backend_discovery_retains_qualified_and_nested_control_endpoints() {
        let backend = |host: &str| Backend::Ssh {
            host: host.into(),
            user: None,
            port: None,
        };
        let first = backend("first");
        let second = backend("second");
        let third = backend("third");
        let instruction = |id, kind| Instruction {
            id: crate::hir::InstructionId(id),
            kind,
            span: Default::default(),
            expansion: Vec::new(),
        };
        let region = Region {
            instructions: vec![
                instruction(
                    0,
                    InstructionKind::Control(Control::Let {
                        slot: crate::hir::control::SlotId(0),
                        value: Expr {
                            ty: Type::Space,
                            kind: ExprKind::Space(Namespace::Qualified {
                                outer: Box::new(Namespace::Endpoint {
                                    backend: first.clone(),
                                }),
                                inner: Box::new(Namespace::Endpoint {
                                    backend: second.clone(),
                                }),
                            }),
                        },
                    }),
                ),
                instruction(
                    1,
                    InstructionKind::Control(Control::If {
                        condition: Expr {
                            ty: Type::Bool,
                            kind: ExprKind::Exists(Box::new(Expr {
                                ty: Type::Path,
                                kind: ExprKind::IntoPath {
                                    value: Box::new(Expr {
                                        ty: Type::Path,
                                        kind: ExprKind::Path {
                                            location: crate::endpoint::Location {
                                                backend: first.clone(),
                                                path: "/input".into(),
                                            },
                                            mode: DestinationMode::Exact,
                                            relocation: None,
                                        },
                                    }),
                                    pwd: None,
                                },
                            })),
                        },
                        then_body: Region {
                            instructions: vec![instruction(
                                2,
                                InstructionKind::Transfer {
                                    source: Input::Literal("bytes".into()),
                                    destination: Output::File {
                                        location: crate::endpoint::Location {
                                            backend: third.clone(),
                                            path: "/output".into(),
                                        },
                                        mode: DestinationMode::Exact,
                                    },
                                },
                            )],
                        },
                        else_body: Region::default(),
                    }),
                ),
            ],
        };
        assert_eq!(backends(&region), vec![&first, &second, &third]);
    }
}
