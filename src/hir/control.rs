//! Typed structured control and lazy range plans; never contains source code.

use serde::{Deserialize, Serialize};

use super::{ProcessId, ReadPort, Region};
use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;
use crate::relocatable::{Namespace, Path as RelocPath};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct SlotId(pub usize);

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Type {
    Bool,
    String,
    Path,
    Space,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Expr {
    pub ty: Type,
    pub kind: ExprKind,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum ExprKind {
    Bool(bool),
    String(String),
    Path {
        location: Location,
        mode: DestinationMode,
        relocation: Option<RelocPath>,
    },
    Space(Namespace),
    ResolvedSpace {
        namespace: Namespace,
        pwd: std::path::PathBuf,
        module_file: Option<std::path::PathBuf>,
    },
    Slot(SlotId),
    Let {
        slot: SlotId,
        value: Box<Expr>,
        body: Box<Expr>,
    },
    Exists(Box<Expr>),
    Not(Box<Expr>),
    Binary {
        left: Box<Expr>,
        op: BinaryOp,
        right: Box<Expr>,
    },
    Join {
        base: Box<Expr>,
        suffix: Box<Expr>,
        mode: DestinationMode,
        #[serde(default)]
        suffix_backend: Option<Backend>,
    },
    Select {
        namespace: Box<Expr>,
        item: Box<Expr>,
    },
    Loop {
        body: Region,
    },
    RangeQuery {
        range: Box<RangeExpr>,
        query: RangeQuery,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BinaryOp {
    And,
    Or,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum ControlInstruction {
    Spawn {
        process: ProcessId,
        executable: Expr,
        arguments: Vec<Expr>,
        cwd: Expr,
    },
    Set {
        slot: SlotId,
        value: Expr,
    },
    Let {
        slot: SlotId,
        value: Expr,
    },
    If {
        condition: Expr,
        then_body: Region,
        else_body: Region,
    },
    For {
        slot: SlotId,
        range: RangeExpr,
        body: Region,
    },
    Loop {
        body: Region,
    },
    Break {
        value: Option<Expr>,
    },
    Delete {
        path: Expr,
    },
    Transfer {
        source: TransferInput,
        destination: TransferOutput,
    },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum TransferInput {
    Scalar(Expr),
    Port { process: ProcessId, port: ReadPort },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum TransferOutput {
    File { path: Expr, mode: DestinationMode },
    Stdin(ProcessId),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RangeExpr {
    pub source: RangeSource,
    pub stages: Vec<RangeStage>,
    pub item_type: Type,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum RangeSource {
    Regex { root: Box<Expr>, pattern: String },
    Values(Vec<Expr>),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum RangeStage {
    Filter { slot: SlotId, predicate: Expr },
    Transform { slot: SlotId, expression: Expr },
    Take(usize),
    Drop(usize),
    Reverse,
    Stride(usize),
    Unique,
    TakeWhile { slot: SlotId, predicate: Expr },
    DropWhile { slot: SlotId, predicate: Expr },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum RangeQuery {
    Contains { value: Box<Expr> },
    FindValue { value: Box<Expr> },
    Empty,
    First,
    Last,
    Any { slot: SlotId, predicate: Box<Expr> },
    All { slot: SlotId, predicate: Box<Expr> },
    None { slot: SlotId, predicate: Box<Expr> },
    Find { slot: SlotId, predicate: Box<Expr> },
}

impl ControlInstruction {
    pub fn name(&self) -> &'static str {
        match self {
            Self::Spawn { .. } => "spawn",
            Self::Set { .. } => "set",
            Self::Let { .. } => "let",
            Self::If { .. } => "if",
            Self::For { .. } => "for",
            Self::Loop { .. } => "loop",
            Self::Break { .. } => "break",
            Self::Delete { .. } => "delete",
            Self::Transfer { .. } => "transfer",
        }
    }
}
