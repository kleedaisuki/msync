//! Narrow co-admission evidence for opposite sides of one native FIFO.

use super::{
    AccessKind, Conflict, EndpointKey, EndpointRole, FileResource, OperationFacts, Resource, Scope,
};
use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionKind, Output};
use crate::language::DestinationMode;
use crate::plan::FileClass;

/// The provider inspected a FIFO inode without opening/consuming its payload.
/// Shape validation is not OS verification: the runtime must retain this exact
/// observation and revalidate the native identity when admitting/opening IO.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FifoEndpointProof {
    key: EndpointKey,
    location: FileResource,
    provider: String,
    identity: String,
}

impl FifoEndpointProof {
    pub fn verified_fifo(
        key: EndpointKey,
        location: Location,
        provider: impl Into<String>,
        identity: impl Into<String>,
    ) -> Result<Self, String> {
        let provider = provider.into();
        let identity = identity.into();
        if !matches!(key.role, EndpointRole::Source | EndpointRole::Destination)
            || location.backend != Backend::Local
            || location.path.is_empty()
            || location.path.contains('\0')
            || provider.is_empty()
            || identity.is_empty()
        {
            return Err("a FIFO endpoint needs a local source/destination and its actual native provider/inode identity".into());
        }
        Ok(Self {
            key,
            location: FileResource::new(location, Scope::Exact),
            provider,
            identity,
        })
    }

    pub fn key(&self) -> EndpointKey {
        self.key
    }
    pub fn location(&self) -> &Location {
        &self.location.location
    }
    pub fn provider(&self) -> &str {
        &self.provider
    }
    pub fn identity(&self) -> &str {
        &self.identity
    }

    pub(crate) fn supports(&self, instruction: &Instruction, facts: OperationFacts) -> bool {
        if self.key.instruction != instruction.id {
            return false;
        }
        let InstructionKind::Transfer {
            source,
            destination,
        } = &instruction.kind
        else {
            return false;
        };
        let location = match (self.key.role, source, destination) {
            (
                EndpointRole::Source,
                Input::File {
                    location,
                    pattern: None,
                },
                _,
            ) if facts.source == FileClass::Stream => location,
            (
                EndpointRole::Destination,
                _,
                Output::File {
                    location,
                    mode: DestinationMode::Exact,
                },
            ) if facts.destination == FileClass::Stream => location,
            _ => return false,
        };
        self.location
            .definitely_same_path(&FileResource::new(location.clone(), Scope::Exact))
    }

    fn covers_access(&self, resource: &Resource) -> bool {
        match resource {
            Resource::File(file) | Resource::StreamPath(file) => {
                file.scope == Scope::Exact && self.location.definitely_same_path(file)
            }
            Resource::StreamIdentity { provider, identity } => {
                provider == &self.provider && identity == &self.identity
            }
            _ => false,
        }
    }
}

/// A write/read pair must open together, not await the other's EOF. This does
/// not make the transfers commute: only their particular FIFO access hazard
/// can await admission. Other file/namespace/observable effects stay ordered.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FifoRendezvousProof {
    writer: FifoEndpointProof,
    reader: FifoEndpointProof,
}

impl FifoRendezvousProof {
    pub fn connect(writer: FifoEndpointProof, reader: FifoEndpointProof) -> Result<Self, String> {
        if writer.key.role != EndpointRole::Destination
            || reader.key.role != EndpointRole::Source
            || writer.key.instruction == reader.key.instruction
            || writer.provider != reader.provider
            || writer.identity != reader.identity
        {
            return Err("FIFO rendezvous needs opposite endpoints in distinct instructions observing the same native inode/provider".into());
        }
        Ok(Self { writer, reader })
    }

    pub fn writer(&self) -> &FifoEndpointProof {
        &self.writer
    }
    pub fn reader(&self) -> &FifoEndpointProof {
        &self.reader
    }

    pub(crate) fn supports(
        &self,
        writer: &Instruction,
        writer_facts: OperationFacts,
        reader: &Instruction,
        reader_facts: OperationFacts,
    ) -> bool {
        self.writer.supports(writer, writer_facts) && self.reader.supports(reader, reader_facts)
    }

    pub(crate) fn matches_conflict(
        &self,
        before: &Instruction,
        after: &Instruction,
        conflict: &Conflict,
    ) -> bool {
        let (left, right, left_kind, right_kind) = if before.id == self.writer.key.instruction
            && after.id == self.reader.key.instruction
        {
            (
                &self.writer,
                &self.reader,
                AccessKind::Write,
                AccessKind::Consume,
            )
        } else if before.id == self.reader.key.instruction
            && after.id == self.writer.key.instruction
        {
            (
                &self.reader,
                &self.writer,
                AccessKind::Consume,
                AccessKind::Write,
            )
        } else {
            return false;
        };
        conflict.before.kind == left_kind
            && conflict.after.kind == right_kind
            && left.covers_access(&conflict.before.resource)
            && right.covers_access(&conflict.after.resource)
    }
}
