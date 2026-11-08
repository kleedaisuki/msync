//! Pure directional scheduling and guarded machine-independent IO candidates.

use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;

use super::{
    BarrierKind, BasicBlock, Input, Instruction, InstructionId, InstructionKind, Output, Region,
};

/// Normalize original directional constraints without permuting IO or crossing
/// native launch/join or structured-control observations. Returned IDs belong only to redundant markers.
pub fn normalize_fences(region: &mut Region) -> Vec<InstructionId> {
    let mut removed = Vec::new();
    let mut result = Vec::with_capacity(region.instructions.len());
    let mut segment = Vec::new();
    for instruction in std::mem::take(&mut region.instructions) {
        if matches!(
            instruction.kind,
            InstructionKind::Transfer { .. } | InstructionKind::Barrier { .. }
        ) {
            segment.push(instruction);
            continue;
        }
        let before_observation = matches!(
            instruction.kind,
            InstructionKind::Spawn { .. } | InstructionKind::Control(_)
        );
        result.extend(normalize_segment(
            std::mem::take(&mut segment),
            before_observation,
            &mut removed,
        ));
        result.push(instruction);
    }
    result.extend(normalize_segment(segment, false, &mut removed));
    region.instructions = result;
    removed
}

struct Placement {
    instruction: Instruction,
    slot: usize,
    lower: usize,
}

fn normalize_segment(
    instructions: Vec<Instruction>,
    before_observation: bool,
    removed: &mut Vec<InstructionId>,
) -> Vec<Instruction> {
    let mut markers = Vec::new();
    let mut transfers = Vec::new();
    let mut lower = 0;
    for instruction in instructions {
        match instruction.kind {
            InstructionKind::Barrier { kind } => {
                markers.push(instruction);
                if kind == BarrierKind::After {
                    lower = markers.len();
                }
            }
            InstructionKind::Transfer { .. } => transfers.push(Placement {
                instruction,
                slot: markers.len(),
                lower,
            }),
            _ => unreachable!("observation controls delimit normalization segments"),
        }
    }
    let upper = upper_bounds(&markers);
    let mut result = Vec::with_capacity(markers.len() + transfers.len());
    let mut markers = markers.into_iter();
    let mut position = 0;
    let mut point = 0;
    // Bounds belong to the original marker order. Deriving new bounds after
    // local swaps can invent constraints and trap the optimizer in a worse cut.
    for (index, transfer) in transfers.into_iter().enumerate() {
        if index == 0 || transfer.lower > point {
            point = upper[transfer.slot];
        }
        while position < point {
            result.push(
                markers
                    .next()
                    .expect("a legal slot is within the marker list"),
            );
            position += 1;
        }
        result.push(transfer.instruction);
    }
    result.extend(markers);
    compact_markers(result, before_observation, removed)
}

fn upper_bounds(markers: &[Instruction]) -> Vec<usize> {
    let mut upper = vec![markers.len(); markers.len() + 1];
    let mut next = markers.len();
    for (index, marker) in markers.iter().enumerate().rev() {
        if matches!(
            marker.kind,
            InstructionKind::Barrier {
                kind: BarrierKind::Before
            }
        ) {
            next = index;
        }
        upper[index] = next;
    }
    upper
}

fn compact_markers(
    instructions: Vec<Instruction>,
    before_observation: bool,
    removed: &mut Vec<InstructionId>,
) -> Vec<Instruction> {
    let mut result = Vec::with_capacity(instructions.len());
    let mut markers = Vec::new();
    for instruction in instructions {
        if matches!(instruction.kind, InstructionKind::Barrier { .. }) {
            markers.push(instruction);
            continue;
        }
        let markers = std::mem::take(&mut markers);
        if result.is_empty() {
            removed.extend(markers.into_iter().map(|marker| marker.id));
        } else {
            result.extend(cut_markers(markers, false, removed));
        }
        result.push(instruction);
    }
    if before_observation {
        result.extend(cut_markers(markers, true, removed));
    } else {
        removed.extend(markers.into_iter().map(|marker| marker.id));
    }
    result
}

fn cut_markers(
    markers: Vec<Instruction>,
    launch: bool,
    removed: &mut Vec<InstructionId>,
) -> Vec<Instruction> {
    let before = markers.iter().position(|marker| {
        matches!(
            marker.kind,
            InstructionKind::Barrier {
                kind: BarrierKind::Before
            }
        )
    });
    let after = before.and_then(|before| {
        markers
            .iter()
            .enumerate()
            .skip(before + 1)
            .find_map(|(index, marker)| {
                matches!(
                    marker.kind,
                    InstructionKind::Barrier {
                        kind: BarrierKind::After
                    }
                )
                .then_some(index)
            })
    });
    let mut result = Vec::new();
    for (index, marker) in markers.into_iter().enumerate() {
        if (launch && before == Some(index))
            || (!launch && after.is_some() && (before == Some(index) || after == Some(index)))
        {
            result.push(marker);
        } else {
            removed.push(marker.id);
        }
    }
    result
}

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
