//! Structured, indentation-sensitive language frontend.
//!
//! The original `syntax::Script` API remains the flat v0.3 compatibility API.
//! This frontend retains literal/path distinctions and explicit scheduling barriers.

mod parser;
mod submission;
mod version;
mod writer;

use crate::syntax::{OperandSource, Span};

pub use parser::{parse, parse_recovering};
pub use submission::{Submission, SubmissionBuffer};
pub use version::{Version, select_version};
pub use writer::format;
pub(crate) use writer::quoted as write_literal;

pub(crate) fn path_boundary_whitespace(path: &str) -> bool {
    path.split(['/', '\\'])
        .any(|component| component.trim() != component)
}

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
    Symbol {
        name: Name,
        parameters: Vec<Name>,
        body: Block,
    },
    Boolean {
        name: Name,
        parameters: Vec<Name>,
        body: Block,
    },
    Delete(Expr),
    Drop(Vec<Name>),
    If {
        condition: Expr,
        then_body: Block,
        else_body: Block,
    },
    For {
        name: Name,
        range: Expr,
        body: Block,
    },
    Loop {
        body: Block,
    },
    Break(Option<Expr>),
    Return(Expr),
    Execute(Expr),
    Include(Expr),
    Call(Expr),
    Global(Vec<Name>),
    /// Explicit scheduling prefixes; the compiler supplies the default join for
    /// unprefixed program bindings, rather than guessing from executable syntax.
    Scheduled {
        asynchronous: bool,
        awaited: bool,
        statement: Box<Statement>,
    },
    /// A start boundary with no payload statement.
    Async,
    /// Join one named process, or all currently pending processes when unnamed.
    Await(Option<Name>),
    Comment(String),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BindingOp {
    Space,
    Value,
    Assign,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Expr {
    pub kind: ExprKind,
    pub span: Span,
    /// Decoded path/literal bytes map to the original suffix token, not its qualifier.
    pub source: OperandSource,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ExprKind {
    Path(PathExpr),
    Literal(String),
    Bool(bool),
    Not(Box<Expr>),
    Exists(Box<Expr>),
    Binary {
        left: Box<Expr>,
        op: BinaryOp,
        right: Box<Expr>,
        operator: Span,
    },
    Loop {
        body: Block,
    },
    Call {
        callee: Box<Expr>,
        arguments: Vec<Expr>,
    },
    Select {
        namespace: Box<Expr>,
        item: Box<Expr>,
        operator: Span,
    },
    Join {
        base: Box<Expr>,
        suffix: Option<Box<Expr>>,
        operator: Span,
        mode: DestinationMode,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BinaryOp {
    And,
    Or,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Pipe,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PathExpr {
    pub qualifier: Option<Name>,
    pub path: String,
    /// A trailing slash denotes a container; `/.` denotes explicit replacement.
    pub mode: DestinationMode,
}

#[derive(
    Clone, Copy, Debug, Default, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize,
)]
#[serde(rename_all = "snake_case")]
pub enum DestinationMode {
    #[default]
    Exact,
    Container,
    ReplaceTree,
}
