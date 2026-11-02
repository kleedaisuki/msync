//! Typed structured control and lazy range plans; never contains source code.

use serde::{Deserialize, Serialize};

use super::{ProcessId, ReadPort, Region};
use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;
use crate::relocatable::{Namespace, Path as RelocPath};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct SlotId(pub usize);

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Type {
    Bool,
    String,
    Path,
    Space,
    Union(ScalarUnion),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(try_from = "Vec<Type>", into = "Vec<Type>")]
pub enum ScalarUnion {
    BoolString,
    BoolPath,
    StringPath,
    BoolStringPath,
}

impl ScalarUnion {
    pub fn members(&self) -> &'static [Type] {
        match self {
            Self::BoolString => &[Type::Bool, Type::String],
            Self::BoolPath => &[Type::Bool, Type::Path],
            Self::StringPath => &[Type::String, Type::Path],
            Self::BoolStringPath => &[Type::Bool, Type::String, Type::Path],
        }
    }

    pub fn iter(&self) -> std::slice::Iter<'static, Type> {
        self.members().iter()
    }

    fn mask(self) -> u8 {
        match self {
            Self::BoolString => 3,
            Self::BoolPath => 5,
            Self::StringPath => 6,
            Self::BoolStringPath => 7,
        }
    }
}

impl From<ScalarUnion> for Vec<Type> {
    fn from(value: ScalarUnion) -> Self {
        value.members().to_vec()
    }
}

impl TryFrom<Vec<Type>> for ScalarUnion {
    type Error = &'static str;

    fn try_from(types: Vec<Type>) -> Result<Self, Self::Error> {
        match types.as_slice() {
            [Type::Bool, Type::String] => Ok(Self::BoolString),
            [Type::Bool, Type::Path] => Ok(Self::BoolPath),
            [Type::String, Type::Path] => Ok(Self::StringPath),
            [Type::Bool, Type::String, Type::Path] => Ok(Self::BoolStringPath),
            _ => Err(
                "a scalar union must list distinct Bool, String or Path members in canonical order",
            ),
        }
    }
}

impl Type {
    pub fn join(types: impl IntoIterator<Item = Self>) -> Option<Self> {
        let mut mask = 0;
        let mut scope = false;
        for ty in types {
            match ty {
                Self::Bool => mask |= 1,
                Self::String => mask |= 2,
                Self::Path => mask |= 4,
                Self::Space => scope = true,
                Self::Union(types) => mask |= types.mask(),
            }
        }
        if scope {
            return (mask == 0).then_some(Self::Space);
        }
        match mask {
            0 => None,
            1 => Some(Self::Bool),
            2 => Some(Self::String),
            3 => Some(Self::Union(ScalarUnion::BoolString)),
            4 => Some(Self::Path),
            5 => Some(Self::Union(ScalarUnion::BoolPath)),
            6 => Some(Self::Union(ScalarUnion::StringPath)),
            _ => Some(Self::Union(ScalarUnion::BoolStringPath)),
        }
    }

    pub fn is_range_item(&self) -> bool {
        true
    }

    pub fn accepts(&self, candidate: &Self) -> bool {
        match (self, candidate) {
            (Self::Union(types), Self::Union(candidates)) => {
                types.mask() & candidates.mask() == candidates.mask()
            }
            (Self::Union(types), candidate) => types.members().contains(candidate),
            _ => self == candidate,
        }
    }
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
    UnboundPath {
        path: String,
        local: Box<Expr>,
    },
    Space(Namespace),
    ResolvedSpace {
        namespace: Namespace,
        pwd: std::path::PathBuf,
        module_file: Option<std::path::PathBuf>,
    },
    Slot(SlotId),
    IntoPath {
        value: Box<Expr>,
        #[serde(default)]
        pwd: Option<std::path::PathBuf>,
    },
    Let {
        slot: SlotId,
        value: Box<Expr>,
        body: Box<Expr>,
    },
    Exists(Box<Expr>),
    Native {
        name: String,
        arguments: Vec<Expr>,
        #[serde(default)]
        pwd: Option<std::path::PathBuf>,
    },
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
    Native {
        name: String,
        arguments: Vec<Expr>,
    },
    RangeTransfer {
        range: RangeExpr,
        destination: Expr,
        mode: DestinationMode,
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
    Let {
        bindings: Vec<(SlotId, Expr)>,
        range: Box<RangeExpr>,
    },
    Native {
        name: String,
        arguments: Vec<Expr>,
        item_type: Type,
        #[serde(default)]
        pwd: Option<std::path::PathBuf>,
    },
    Regex {
        root: Box<Expr>,
        pattern: String,
    },
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
            Self::Native { .. } => "native",
            Self::RangeTransfer { .. } => "range_transfer",
            Self::Transfer { .. } => "transfer",
        }
    }
}
