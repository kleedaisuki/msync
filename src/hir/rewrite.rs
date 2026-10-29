//! Machine-independent candidates. Recognition never authorizes an IO rewrite.

use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;

use super::{BasicBlock, Input, Instruction, InstructionId, InstructionKind, Output, Region};

/// Both original instructions remain in the IR until an ordinary runtime owner
/// pins the surviving value and proves its type, alias and namespace guards.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AdjacentOverwrite {
    pub first: InstructionId,
    pub last: InstructionId,
}

impl AdjacentOverwrite {
    pub fn recognize(first: &Instruction, last: &Instruction) -> Option<Self> {
        let (first_source, first_target) = exact(first)?;
        let (last_source, last_target) = exact(last)?;
        if first_target != last_target
            || !matches!(first_target.backend, Backend::Local | Backend::Env)
            || !source(first_source, false)
            || !source(last_source, true)
        {
            return None;
        }
        Some(Self {
            first: first.id,
            last: last.id,
        })
    }
}

pub fn adjacent_overwrites(region: &Region) -> impl Iterator<Item = AdjacentOverwrite> + '_ {
    region.blocks().flat_map(adjacent_overwrites_in)
}

pub fn adjacent_overwrites_in(
    block: BasicBlock<'_>,
) -> impl Iterator<Item = AdjacentOverwrite> + '_ {
    block
        .instructions
        .windows(2)
        .filter_map(|pair| AdjacentOverwrite::recognize(&pair[0], &pair[1]))
}

pub fn overwrite_run(instructions: &[Instruction]) -> Option<&[Instruction]> {
    let length = 1 + instructions
        .windows(2)
        .take_while(|pair| AdjacentOverwrite::recognize(&pair[0], &pair[1]).is_some())
        .count();
    (length > 1).then(|| &instructions[..length])
}

fn source(input: &Input, remote: bool) -> bool {
    match input {
        Input::Literal(_) => true,
        Input::File {
            location,
            pattern: None,
        } => location.backend == Backend::Local || (remote && location.backend == Backend::Wsl),
        _ => false,
    }
}

fn exact(node: &Instruction) -> Option<(&Input, &Location)> {
    match &node.kind {
        InstructionKind::Transfer {
            source,
            destination:
                Output::File {
                    location,
                    mode: DestinationMode::Exact,
                },
        } => Some((source, location)),
        _ => None,
    }
}
