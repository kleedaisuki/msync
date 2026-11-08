use super::state::State;
use super::{Diagnostic, Observations, Operand, endpoint_error, parents, validate};
use crate::endpoint::{Backend, Location};
use crate::hir::effects::OperationFacts;
use crate::hir::graph::PlanningContext;
use crate::hir::{Input, Instruction, InstructionKind, Output, ReadPort, Region};
use crate::language::DestinationMode;
use crate::plan::FileClass;

pub(super) struct Types<'a> {
    pub(super) observations: &'a Observations,
    pub(super) context: &'a mut PlanningContext,
    pub(super) errors: Vec<Diagnostic>,
    pub(super) live: bool,
}

impl Types<'_> {
    fn class(&self, state: &State, location: &Location) -> FileClass {
        state.class(location, &self.observations.classes)
    }

    pub(super) fn region(&mut self, region: &Region, state: &mut State) {
        for node in &region.instructions {
            match &node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => self.transfer(node, source, destination, state),
                InstructionKind::Spawn { executable, .. } => {
                    let class = self.class(state, executable);
                    if matches!(class, FileClass::Directory | FileClass::Stream)
                        || class == FileClass::Missing
                            && (self.live || executable.backend == Backend::Local)
                    {
                        self.errors.push(Diagnostic::at(
                            node,
                            Operand::Operation,
                            endpoint_error(
                                "program executable is missing or is not an ordinary file",
                                executable,
                                node.span,
                            ),
                        ));
                    }
                    state.unknown();
                }
                InstructionKind::Control(_) => state.unknown(),
                InstructionKind::Barrier { .. } | InstructionKind::Await { .. } => {}
            }
        }
    }

    fn transfer(
        &mut self,
        node: &Instruction,
        source: &Input,
        destination: &Output,
        state: &mut State,
    ) {
        let pinned_file = matches!(source, Input::File { location, pattern: None }
            if self.context.pinned_inputs.get(&node.id).is_some_and(|proof| proof.covers(location)));
        let inferred = OperationFacts {
            source: match source {
                Input::File { .. } if pinned_file => FileClass::Regular,
                Input::File { location, .. } => self.class(state, location),
                Input::Literal(_) => FileClass::Regular,
                Input::Port {
                    port: ReadPort::Stdout | ReadPort::Stderr,
                    ..
                } => FileClass::Stream,
                Input::Port { .. } => FileClass::Regular,
            },
            destination: match destination {
                Output::File { location, .. } => self.class(state, location),
                Output::Stdin(_) => FileClass::Stream,
            },
        };
        let facts = self
            .context
            .operation_facts
            .entry(node.id)
            .or_insert(inferred);
        if pinned_file {
            facts.source = FileClass::Regular;
        }
        let facts = *facts;
        if let Output::File { location, .. } = destination
            && let Some(frontier) = parents(location)
                .into_iter()
                .find(|parent| self.class(state, parent) == FileClass::Directory)
        {
            self.context
                .existing_parent_frontiers
                .entry(node.id)
                .or_insert(frontier);
        }
        if let Err(error) = validate(node, source, destination, facts, self.live) {
            self.errors.push(error);
            return;
        }
        let Output::File { location, mode } = destination else {
            return;
        };
        let tree = facts.source == FileClass::Directory || *mode != DestinationMode::Exact;
        let kind = if *mode == DestinationMode::Container {
            FileClass::Directory
        } else if facts.destination == FileClass::Stream {
            FileClass::Stream
        } else if facts.source == FileClass::Directory || *mode == DestinationMode::ReplaceTree {
            FileClass::Directory
        } else if facts.source == FileClass::Unknown {
            FileClass::Unknown
        } else {
            FileClass::Regular
        };
        if facts.destination != FileClass::Stream {
            for parent in parents(location) {
                state.write(parent.clone(), false, FileClass::Directory);
            }
        }
        state.write(location.clone(), tree, kind);
    }
}
