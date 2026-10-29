//! Typed, expanded language IR. No resource inspection or execution occurs here.

pub mod effects;
pub mod graph;
pub mod rewrite;

use std::path::PathBuf;
use std::sync::Arc;

use crate::endpoint::Location;
use crate::language::DestinationMode;
use crate::syntax::Span;

#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, serde::Serialize, serde::Deserialize,
)]
pub struct InstructionId(pub usize);

#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, serde::Serialize, serde::Deserialize,
)]
pub struct ProcessId(pub usize);

#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Program {
    pub body: Region,
}

#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Region {
    pub instructions: Vec<Instruction>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BlockId(pub usize);

/// A serial interpretation boundary; its operations may be scheduled as a DAG.
#[derive(Clone, Copy, Debug)]
pub struct BasicBlock<'a> {
    pub id: BlockId,
    pub instructions: &'a [Instruction],
}

pub struct Blocks<'a> {
    remaining: &'a [Instruction],
    next: usize,
}

impl Program {
    pub fn blocks(&self) -> Blocks<'_> {
        self.body.blocks()
    }
}

impl Region {
    pub fn blocks(&self) -> Blocks<'_> {
        Blocks {
            remaining: &self.instructions,
            next: 0,
        }
    }
}

impl<'a> Iterator for Blocks<'a> {
    type Item = BasicBlock<'a>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining.is_empty() {
            return None;
        }
        let mut end = self.remaining.len();
        for (index, instruction) in self.remaining.iter().enumerate() {
            match instruction.kind {
                InstructionKind::Barrier {
                    kind: BarrierKind::Before,
                } if index > 0 => {
                    end = index;
                    break;
                }
                InstructionKind::Await { .. } if index > 0 => {
                    end = index;
                    break;
                }
                InstructionKind::Barrier {
                    kind: BarrierKind::After,
                }
                | InstructionKind::Spawn { .. }
                | InstructionKind::Await { .. } => {
                    end = index + 1;
                    break;
                }
                _ => {}
            }
        }
        let (instructions, remaining) = self.remaining.split_at(end);
        self.remaining = remaining;
        let id = BlockId(self.next);
        self.next += 1;
        Some(BasicBlock { id, instructions })
    }
}

impl std::iter::FusedIterator for Blocks<'_> {}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Instruction {
    pub id: InstructionId,
    pub kind: InstructionKind,
    pub span: Span,
    /// Macro definition sites, outermost first; `span` remains the call site.
    pub expansion: Vec<Span>,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
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
    Barrier {
        kind: BarrierKind,
    },
    Await {
        process: Option<ProcessId>,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BarrierKind {
    Before,
    After,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
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

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum Output {
    File {
        location: Location,
        mode: DestinationMode,
    },
    Stdin(ProcessId),
}

#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, serde::Serialize, serde::Deserialize,
)]
#[serde(rename_all = "snake_case")]
pub enum ReadPort {
    Stdout,
    Stderr,
    ExitCode,
    Pid,
    Meta,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum Argument {
    Text(String),
    Path(Location),
}
