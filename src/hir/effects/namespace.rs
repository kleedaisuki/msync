//! Trusted native namespace mutation permits, not general metadata commutativity.

use super::{FileResource, OperationFacts, Scope};
use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionKind, Output};
use crate::language::DestinationMode;
use crate::plan::FileClass;
use crate::transaction::ResourceKey;

/// One existing directory's active Manager merge epoch. The provider binds its
/// identity and unchanged permissions, canonical NativeNameIndex keys, actual
/// reversible mutation receipts and logical-order timestamp-selection policy.
///
/// Only successful file insertions contribute a timestamp. The winning stamp
/// is the actual mutation receipt with greatest logical PublicationOrder, not
/// max(wall-clock, old mtime). A metadata/predicate/tree/permission barrier closes
/// the epoch. This constructor validates shape; it does not establish that the
/// native Manager implements or still owns this capability.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct NamespaceMergeEpoch {
    provider: String,
    id: String,
    parent_key: ResourceKey,
}

impl NamespaceMergeEpoch {
    pub fn verified(
        provider: impl Into<String>,
        id: impl Into<String>,
        parent_key: ResourceKey,
    ) -> Result<Self, String> {
        let provider = provider.into();
        let id = id.into();
        if provider.is_empty() || id.is_empty() {
            return Err("a namespace merge epoch needs its actual Manager identity and active native-directory epoch".into());
        }
        Ok(Self {
            provider,
            id,
            parent_key,
        })
    }

    pub fn provider(&self) -> &str {
        &self.provider
    }
    pub fn id(&self) -> &str {
        &self.id
    }
    pub fn parent_key(&self) -> &ResourceKey {
        &self.parent_key
    }
}

/// Authority for one implicit direct-child regular-file insertion. Both keys
/// come from the Manager's native alias/case index, never a basename guess.
/// The runtime retains the real permit and commits through its receipt method;
/// cloning this pure certificate does not authorize another native mutation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NamespaceMergeProof {
    epoch: NamespaceMergeEpoch,
    parent: FileResource,
    target: FileResource,
    child_key: ResourceKey,
    logical_order: u64,
}

impl NamespaceMergeProof {
    pub fn insert_file(
        epoch: NamespaceMergeEpoch,
        parent: Location,
        target: Location,
        child_key: ResourceKey,
        logical_order: u64,
    ) -> Result<Self, String> {
        let parent_key = &epoch.parent_key;
        let direct_key = child_key.namespace() == parent_key.namespace()
            && child_key.components().len() == parent_key.components().len() + 1
            && child_key.components().starts_with(parent_key.components());
        if !direct_key
            || !matches!(parent.backend, Backend::Local)
            || !matches!(target.backend, Backend::Local)
            || parent.path.is_empty()
            || target.path.is_empty()
            || parent.path.contains('\0')
            || target.path.contains('\0')
        {
            return Err("a namespace insertion needs a verified existing local parent and one canonical native direct-child key".into());
        }
        let parent = FileResource::new(parent, Scope::Exact);
        let target = FileResource::new(target, Scope::Exact);
        let raw_parent = std::path::Path::new(&target.location.path)
            .parent()
            .is_some_and(|expected| expected == std::path::Path::new(&parent.location.path));
        if !raw_parent
            && target
                .parent()
                .is_none_or(|expected| !expected.definitely_same_path(&parent))
        {
            return Err("the namespace insertion's logical target must be directly below its certified parent".into());
        }
        Ok(Self {
            epoch,
            parent,
            target,
            child_key,
            logical_order,
        })
    }

    pub fn epoch(&self) -> &NamespaceMergeEpoch {
        &self.epoch
    }
    pub fn parent(&self) -> &Location {
        &self.parent.location
    }
    pub fn target(&self) -> &Location {
        &self.target.location
    }
    pub fn child_key(&self) -> &ResourceKey {
        &self.child_key
    }
    pub fn logical_order(&self) -> u64 {
        self.logical_order
    }
    pub fn covers(&self, target: &Location) -> bool {
        self.target
            .definitely_same_path(&FileResource::new(target.clone(), Scope::Exact))
    }
    pub fn parent_is(&self, parent: &Location) -> bool {
        self.parent
            .definitely_same_path(&FileResource::new(parent.clone(), Scope::Exact))
    }

    pub fn commutes_with(&self, other: &Self) -> bool {
        self.epoch == other.epoch
            && self.child_key != other.child_key
            && self.logical_order != other.logical_order
    }

    pub fn supports(&self, instruction: &Instruction, facts: OperationFacts) -> bool {
        let InstructionKind::Transfer {
            source,
            destination:
                Output::File {
                    location,
                    mode: DestinationMode::Exact,
                },
        } = &instruction.kind
        else {
            return false;
        };
        if facts.destination != FileClass::Missing || !self.covers(location) {
            return false;
        }
        match source {
            Input::Literal(_) => true,
            Input::File {
                location,
                pattern: None,
            } => matches!(
                super::file_class(location, facts.source),
                FileClass::Regular | FileClass::Stream
            ),
            _ => false,
        }
    }

    pub(super) fn same_native_parent(&self, other: &Self) -> bool {
        self.epoch.provider == other.epoch.provider
            && self.epoch.parent_key == other.epoch.parent_key
    }

    pub(super) fn entry_overlaps(&self, other: &Self) -> bool {
        if self.same_native_parent(other) {
            return self.child_key == other.child_key;
        }
        self.target.may_overlap(&other.target)
    }

    pub(super) fn parent_resource(&self) -> &FileResource {
        &self.parent
    }
    pub(super) fn target_resource(&self) -> &FileResource {
        &self.target
    }
}
