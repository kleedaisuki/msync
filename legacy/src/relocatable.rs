//! Machine-independent contextual paths and relocation records.

use serde::{Deserialize, Serialize};

use crate::endpoint::Backend;
use crate::hir::{InstructionId, ProcessId, ReadPort};
use crate::language::DestinationMode;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Anchor {
    WorkingDirectory,
    Home,
    ModuleFile { module: String },
    ModuleDirectory { module: String },
    Environment { name: String },
    SearchPath { name: String },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Path {
    pub anchor: Anchor,
    pub suffix: String,
}

impl Path {
    pub fn new(anchor: Anchor, suffix: impl Into<String>) -> Self {
        Self {
            anchor,
            suffix: suffix.into(),
        }
    }

    pub fn append(&mut self, suffix: &str) {
        if suffix.is_empty() {
            return;
        }
        if self.suffix.is_empty() {
            self.suffix = suffix.trim_start_matches(['/', '\\']).to_owned();
        } else {
            self.suffix.push_str(suffix);
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Slot {
    Source,
    Destination,
    Executable,
    Argument { index: usize },
    Cwd,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Relocation {
    pub instruction: InstructionId,
    pub slot: Slot,
    pub path: Path,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Namespace {
    Lexical {
        members: std::collections::BTreeMap<String, crate::hir::control::Expr>,
    },
    WorkingDirectory,
    Module,
    Endpoint {
        backend: Backend,
    },
    Qualified {
        outer: Box<Namespace>,
        inner: Box<Namespace>,
    },
    Api {
        scope: BuiltinScope,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BuiltinScope {
    View,
    Std,
    Fs,
    Path,
    Bool,
    String,
    Ranges,
}

impl BuiltinScope {
    pub fn module_name(self) -> Option<&'static str> {
        match self {
            Self::View | Self::Std => None,
            Self::Fs => Some("fs"),
            Self::Path => Some("path"),
            Self::Bool => Some("bool"),
            Self::String => Some("string"),
            Self::Ranges => Some("ranges"),
        }
    }

    pub fn from_module_name(name: &str) -> Option<Self> {
        match name {
            "fs" => Some(Self::Fs),
            "path" => Some(Self::Path),
            "bool" => Some(Self::Bool),
            "string" => Some(Self::String),
            "ranges" => Some(Self::Ranges),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ExportValue {
    Bool {
        value: bool,
    },
    Dynamic {
        expression: crate::hir::control::Expr,
    },
    Range {
        range: crate::hir::control::RangeExpr,
    },
    Literal {
        text: String,
    },
    Path {
        backend: Option<Backend>,
        path: String,
        relocation: Option<Path>,
        is_file: bool,
        mode: DestinationMode,
    },
    Namespace {
        namespace: Namespace,
    },
    Process {
        process: ProcessId,
    },
    Port {
        process: ProcessId,
        port: Option<ReadPort>,
    },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Export {
    pub name: String,
    pub value: ExportValue,
}
