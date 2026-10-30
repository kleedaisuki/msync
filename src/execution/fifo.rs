//! Block-local native FIFO evidence, backed by retained generation leases.

use std::collections::BTreeMap;
use std::path::Path;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::{EndpointKey, EndpointRole, FifoEndpointProof, FifoRendezvousProof};
use crate::hir::graph::PlanningContext;
use crate::hir::{BasicBlock, Input, InstructionId, InstructionKind, Output};
use crate::language::DestinationMode;
use crate::plan::FileClass;
use crate::streams::{FifoIdentity, FifoPins};
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) struct FifoState {
    pins: BTreeMap<EndpointKey, FifoIdentity>,
    groups: Vec<Vec<InstructionId>>,
}

impl FifoState {
    pub fn inspect(block: BasicBlock<'_>, context: &mut PlanningContext) -> Result<Self> {
        // A previous block's endpoint generations are never premises for this one.
        context.physical_fifos.clear();
        context.physical_rendezvous.clear();
        let mut state = Self {
            pins: BTreeMap::new(),
            groups: Vec::new(),
        };
        for node in block.instructions {
            let InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            else {
                continue;
            };
            if let Input::File {
                location,
                pattern: None,
            } = source
            {
                state.observe(node.id, EndpointRole::Source, location, node.span, context)?;
            }
            if let Output::File {
                location,
                mode: DestinationMode::Exact,
            } = destination
            {
                state.observe(
                    node.id,
                    EndpointRole::Destination,
                    location,
                    node.span,
                    context,
                )?;
            }
        }
        let mut channels =
            BTreeMap::<String, (Vec<FifoEndpointProof>, Vec<FifoEndpointProof>)>::new();
        for node in block.instructions {
            for role in [EndpointRole::Destination, EndpointRole::Source] {
                let Some(proof) = context.physical_fifos.get(&EndpointKey {
                    instruction: node.id,
                    role,
                }) else {
                    continue;
                };
                let channel = channels.entry(proof.identity().to_owned()).or_default();
                match role {
                    EndpointRole::Destination => channel.0.push(proof.clone()),
                    EndpointRole::Source => channel.1.push(proof.clone()),
                    EndpointRole::Executable => unreachable!(),
                }
            }
        }
        for (writers, readers) in channels.into_values() {
            if writers.is_empty() || readers.is_empty() {
                continue;
            }
            if writers.len() != readers.len() {
                return Err(ExecutionError::new("a FIFO rendezvous has unequal writer and reader counts", block.instructions[0].span)
                    .help("Use one writer and one reader per FIFO wave, or separate independent flows onto distinct FIFOs."));
            }
            for (writer, reader) in writers.into_iter().zip(readers) {
                let proof = FifoRendezvousProof::connect(writer, reader)
                    .map_err(|message| ExecutionError::new(message, block.instructions[0].span))?;
                context.physical_rendezvous.push(proof);
            }
        }
        state.groups = crate::hir::graph::physical_groups(&context.physical_rendezvous);
        Ok(state)
    }

    pub fn groups(&self) -> &[Vec<InstructionId>] {
        &self.groups
    }

    fn observe(
        &mut self,
        instruction: InstructionId,
        role: EndpointRole,
        location: &Location,
        span: Span,
        context: &mut PlanningContext,
    ) -> Result<()> {
        if !cfg!(unix) {
            return Ok(());
        }
        if location.backend != Backend::Local {
            return Ok(());
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::FileTypeExt;
            match std::fs::symlink_metadata(&location.path) {
                Ok(metadata) if metadata.file_type().is_fifo() => {}
                _ => return Ok(()),
            }
        }
        let path = super::domain::normalize(Path::new(&location.path), span)?;
        let pin = match crate::streams::fifo_identity(&path) {
            Ok(Some(pin)) => pin,
            Ok(None) => return Ok(()),
            Err(cause) if cause.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(cause) => {
                return Err(ExecutionError::caused(
                    "cannot inspect FIFO generation",
                    span,
                    cause,
                ));
            }
        };
        pin.require_generation_pin().map_err(|cause| {
            ExecutionError::caused(
                "cannot retain FIFO generation before co-admission",
                span,
                cause,
            )
        })?;
        let key = EndpointKey { instruction, role };
        let proof =
            FifoEndpointProof::verified_fifo(key, location.clone(), "native-fifo", pin.native_id())
                .map_err(|message| ExecutionError::new(message, span))?;
        let facts = context.operation_facts.entry(instruction).or_default();
        match role {
            EndpointRole::Source => facts.source = FileClass::Stream,
            EndpointRole::Destination => facts.destination = FileClass::Stream,
            EndpointRole::Executable => unreachable!(),
        }
        context.physical_fifos.insert(key, proof);
        self.pins.insert(key, pin);
        Ok(())
    }

    pub fn pins(&self, instruction: InstructionId) -> FifoPins {
        FifoPins {
            source: self
                .pins
                .get(&EndpointKey {
                    instruction,
                    role: EndpointRole::Source,
                })
                .cloned(),
            destination: self
                .pins
                .get(&EndpointKey {
                    instruction,
                    role: EndpointRole::Destination,
                })
                .cloned(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_empty_block_has_no_fifo_generation() {
        let mut context = PlanningContext::default();
        let block = BasicBlock {
            id: crate::hir::BlockId(0),
            instructions: &[],
        };
        let state = FifoState::inspect(block, &mut context).unwrap();
        assert!(!state.pins(InstructionId(0)).any());
        assert!(context.physical_fifos.is_empty());
        assert!(context.physical_rendezvous.is_empty());
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn replacing_fifo_cannot_reuse_a_leased_inode_generation() {
        use std::ffi::CString;
        use std::os::unix::ffi::OsStrExt;
        let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&scratch).unwrap();
        let temp = tempfile::Builder::new()
            .prefix("fifo-generation-")
            .tempdir_in(scratch)
            .unwrap();
        let path = temp.path().join("pipe");
        let native = CString::new(path.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(native.as_ptr(), 0o600) }, 0);
        let old = crate::streams::fifo_identity(&path).unwrap().unwrap();
        old.require_generation_pin().unwrap();
        std::fs::remove_file(&path).unwrap();
        assert_eq!(unsafe { libc::mkfifo(native.as_ptr(), 0o600) }, 0);
        let new = crate::streams::fifo_identity(&path).unwrap().unwrap();
        assert_ne!(old, new);
    }
}
