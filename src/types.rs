//! Inferred language types and the capabilities required by operators.

use std::fmt;

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Type {
    Bool,
    String,
    Path,
    Scope,
    Process,
    ReadPort,
    WritePort,
    Range(crate::hir::control::Type),
    Union(crate::hir::control::ScalarUnion),
    Unit,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OperatorTrait {
    Join,
    Select,
    Logical,
    Equality,
    Ordering,
    Transfer,
}

impl Type {
    pub fn union(types: impl IntoIterator<Item = Type>) -> Option<Self> {
        let mut result = None;
        for ty in types {
            result = Some(match result {
                None => ty,
                Some(previous) if previous == ty => previous,
                Some(previous) => {
                    let joined =
                        crate::hir::control::Type::join([previous.control()?, ty.control()?])?;
                    Self::from_control(&joined)
                }
            });
        }
        result
    }

    fn control(&self) -> Option<crate::hir::control::Type> {
        use crate::hir::control::Type as Control;
        match self {
            Self::Bool => Some(Control::Bool),
            Self::String => Some(Control::String),
            Self::Path => Some(Control::Path),
            Self::Scope => Some(Control::Space),
            Self::Union(members) => Some(Control::Union(*members)),
            _ => None,
        }
    }

    pub fn from_control(ty: &crate::hir::control::Type) -> Self {
        use crate::hir::control::Type as Control;
        match ty {
            Control::Bool => Self::Bool,
            Control::String => Self::String,
            Control::Path => Self::Path,
            Control::Space => Self::Scope,
            Control::Union(members) => Self::Union(*members),
        }
    }

    fn readable(&self) -> bool {
        match self {
            Self::Path | Self::String | Self::ReadPort => true,
            Self::Range(item) => Self::from_control(item).readable(),
            Self::Union(members) => members
                .iter()
                .all(|item| Self::from_control(item).readable()),
            _ => false,
        }
    }
}

impl OperatorTrait {
    pub fn result(self, left: &Type, right: &Type) -> Option<Type> {
        if let Type::Union(members) = left {
            let mut result = None;
            for member in members.iter() {
                let output = self.result(&Type::from_control(member), right)?;
                result = Some(Type::union(result.into_iter().chain(Some(output)))?);
            }
            return result;
        }
        if let Type::Union(members) = right {
            let mut result = None;
            for member in members.iter() {
                let output = self.result(left, &Type::from_control(member))?;
                result = Some(Type::union(result.into_iter().chain(Some(output)))?);
            }
            return result;
        }
        use Type as T;
        match (self, left, right) {
            (Self::Join, T::String | T::Path, T::String | T::Path) => Some(T::Path),
            (Self::Select, T::Scope, T::String | T::Path) => Some(T::Path),
            (Self::Select, T::Scope, T::Scope) => Some(T::Scope),
            (Self::Logical, T::Bool, T::Bool) => Some(T::Bool),
            (Self::Equality, T::Bool, T::Bool) | (Self::Equality, T::String, T::String) => {
                Some(T::Bool)
            }
            (Self::Ordering, T::String, T::String) => Some(T::Bool),
            (Self::Transfer, source, T::Path | T::WritePort) if source.readable() => Some(T::Unit),
            _ => None,
        }
    }
}

impl fmt::Display for Type {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Bool => output.write_str("Bool"),
            Self::String => output.write_str("String"),
            Self::Path => output.write_str("Path"),
            Self::Scope => output.write_str("Scope"),
            Self::Process => output.write_str("Process"),
            Self::ReadPort => output.write_str("ReadPort"),
            Self::WritePort => output.write_str("WritePort"),
            Self::Range(item) => write!(output, "Range<{}>", Self::from_control(item)),
            Self::Union(members) => {
                for (index, member) in members.iter().enumerate() {
                    if index != 0 {
                        output.write_str(" | ")?;
                    }
                    write!(output, "{}", Self::from_control(member))?;
                }
                Ok(())
            }
            Self::Unit => output.write_str("Unit"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_join_and_scope_selection_have_explicit_results() {
        for left in [Type::String, Type::Path] {
            for right in [Type::String, Type::Path] {
                assert_eq!(OperatorTrait::Join.result(&left, &right), Some(Type::Path));
            }
        }
        for right in [Type::String, Type::Path, Type::Scope] {
            let expected = if right == Type::Scope {
                Type::Scope
            } else {
                Type::Path
            };
            assert_eq!(
                OperatorTrait::Select.result(&Type::Scope, &right),
                Some(expected)
            );
        }
        assert_eq!(OperatorTrait::Join.result(&Type::Bool, &Type::Path), None);
        assert_eq!(
            OperatorTrait::Select.result(&Type::Path, &Type::String),
            None
        );
    }

    #[test]
    fn a_union_requires_every_member_to_support_the_operator() {
        let fragments = Type::union([Type::String, Type::Path]).unwrap();
        assert_eq!(
            OperatorTrait::Join.result(&fragments, &Type::String),
            Some(Type::Path)
        );
        let mixed = Type::union([Type::String, Type::Bool]).unwrap();
        assert_eq!(OperatorTrait::Join.result(&mixed, &Type::String), None);
        assert_eq!(OperatorTrait::Logical.result(&mixed, &Type::Bool), None);
    }
}
