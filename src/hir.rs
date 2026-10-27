//! Typed, expanded language IR. No resource inspection or execution occurs here.

pub mod effects;
pub mod graph;

use std::path::PathBuf;
use std::sync::Arc;

use crate::endpoint::Location;
use crate::language::{DestinationMode, Isolation};
use crate::syntax::Span;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct InstructionId(pub usize);

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ProcessId(pub usize);

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct TransactionId(pub usize);

#[derive(Clone, Debug, Default)]
pub struct Program {
    pub body: Region,
    pub transactions: Vec<TransactionInfo>,
}

#[derive(Clone, Debug, Default)]
pub struct Region {
    pub instructions: Vec<Instruction>,
}

#[derive(Clone, Debug)]
pub struct TransactionInfo {
    pub id: TransactionId,
    pub name: String,
    pub isolation: Isolation,
    pub span: Span,
}

#[derive(Clone, Debug)]
pub struct Instruction {
    pub id: InstructionId,
    pub kind: InstructionKind,
    pub span: Span,
    /// Macro definition sites, outermost first; `span` remains the call site.
    pub expansion: Vec<Span>,
}

#[derive(Clone, Debug)]
pub enum InstructionKind {
    Transfer {
        source: Input,
        destination: Output,
    },
    Spawn {
        process: ProcessId,
        executable: Location,
        arguments: Vec<Argument>,
        cwd: PathBuf,
    },
    Transaction {
        transaction: TransactionId,
        body: Region,
    },
    Commit {
        transaction: TransactionId,
    },
    Rollback {
        transaction: TransactionId,
        scope: RollbackScope,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RollbackScope {
    Checkpoint,
    Begin,
}

#[derive(Clone, Debug)]
pub enum Input {
    File {
        location: Location,
        pattern: Option<String>,
    },
    Literal(Arc<str>),
    Port {
        process: ProcessId,
        port: ReadPort,
    },
}

#[derive(Clone, Debug)]
pub enum Output {
    File {
        location: Location,
        mode: DestinationMode,
    },
    Stdin(ProcessId),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ReadPort {
    Stdout,
    Stderr,
    ExitCode,
    Pid,
    Meta,
}

#[derive(Clone, Debug)]
pub enum Argument {
    Text(String),
    Path(Location),
}
