//! Structured, indentation-sensitive language frontend.
//!
//! The original `syntax::Script` API remains the flat v0.3 compatibility API.
//! This frontend retains literal/path distinctions and nested control regions.

mod parser;
mod writer;

use crate::syntax::Span;

pub use parser::parse;
pub use writer::format;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Document {
    pub statements: Vec<Statement>,
}

pub type Block = Vec<Statement>;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Statement {
    pub kind: StatementKind,
    pub span: Span,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Name {
    pub text: String,
    pub span: Span,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StatementKind {
    Binding {
        name: Name,
        op: BindingOp,
        value: Expr,
    },
    Transfer {
        source: Expr,
        destination: Expr,
    },
    Macro {
        name: Name,
        parameters: Vec<Name>,
        body: Block,
    },
    Call(Expr),
    Global(Vec<Name>),
    Transaction {
        name: Name,
        isolation: Isolation,
        body: Block,
    },
    Commit,
    Rollback(Option<Name>),
    Comment(String),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BindingOp {
    Space,
    Value,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Isolation {
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    #[default]
    Serializable,
}

impl Isolation {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::ReadUncommitted => "read uncommitted",
            Self::ReadCommitted => "read committed",
            Self::RepeatableRead => "repeatable read",
            Self::Serializable => "serializable",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Expr {
    pub kind: ExprKind,
    pub span: Span,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ExprKind {
    Path(PathExpr),
    Literal(String),
    Call {
        callee: Box<Expr>,
        arguments: Vec<Expr>,
    },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PathExpr {
    pub qualifier: Option<Name>,
    pub path: String,
    /// A trailing slash denotes a container; `/.` denotes explicit replacement.
    pub mode: DestinationMode,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum DestinationMode {
    #[default]
    Exact,
    Container,
    ReplaceTree,
}
