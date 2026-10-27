use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::sync::Arc;

use crate::hir::TransactionId;
use crate::language::Isolation;

pub type Value = Arc<[u8]>;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord)]
pub struct Epoch(pub u64);

/// An absolute logical resource identity, after the adapter resolves aliases.
///
/// Namespaces identify physical stores (including volume/remote-host identity).
/// Components use `/`; native adapters must normalize platform spellings before
/// constructing a key. This type does not canonicalize or probe a filesystem.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ResourceKey {
    namespace: String,
    components: Vec<String>,
}

impl ResourceKey {
    pub fn new(namespace: impl Into<String>, path: &str) -> Result<Self, StoreError> {
        let namespace = namespace.into();
        if namespace.is_empty() || namespace.contains('\0') || !path.starts_with('/') {
            return Err(StoreError::InvalidResource {
                namespace,
                path: path.into(),
                reason: "provide a nonempty namespace and an absolute logical path",
            });
        }
        let mut components = Vec::new();
        for part in path.split('/') {
            match part {
                "" | "." => {}
                ".." if components.pop().is_some() => {}
                ".." => {
                    return Err(StoreError::InvalidResource {
                        namespace,
                        path: path.into(),
                        reason: "the logical path escapes its namespace root",
                    });
                }
                part if part.contains('\0') => {
                    return Err(StoreError::InvalidResource {
                        namespace,
                        path: path.into(),
                        reason: "resource components cannot contain NUL",
                    });
                }
                part => components.push(part.to_owned()),
            }
        }
        Ok(Self {
            namespace,
            components,
        })
    }

    pub fn namespace(&self) -> &str {
        &self.namespace
    }

    pub fn components(&self) -> &[String] {
        &self.components
    }

    pub fn tree(&self) -> ResourcePrefix {
        ResourcePrefix { root: self.clone() }
    }
}

impl fmt::Display for ResourceKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}::/{}", self.namespace, self.components.join("/"))
    }
}

/// An inclusive subtree predicate; component boundaries prevent `/a` matching `/ab`.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ResourcePrefix {
    root: ResourceKey,
}

impl ResourcePrefix {
    pub fn root(&self) -> &ResourceKey {
        &self.root
    }

    pub fn contains(&self, key: &ResourceKey) -> bool {
        key.namespace == self.root.namespace && key.components.starts_with(&self.root.components)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TransactionState {
    Active,
    Finished,
    RolledBack,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConflictKind {
    Write,
    Read,
    Predicate,
    Origin,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StoreError {
    InvalidResource {
        namespace: String,
        path: String,
        reason: &'static str,
    },
    DuplicateTransaction(TransactionId),
    UnknownTransaction(TransactionId),
    ActiveTransaction(TransactionId),
    InactiveTransaction {
        transaction: TransactionId,
        state: TransactionState,
    },
    Conflict {
        transaction: TransactionId,
        other: Option<TransactionId>,
        resource: ResourceKey,
        kind: ConflictKind,
        observed: Option<Epoch>,
        actual: Option<Epoch>,
    },
    CounterOverflow,
}

impl fmt::Display for StoreError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidResource {
                namespace,
                path,
                reason,
            } => {
                write!(f, "invalid resource {namespace}::{path}: {reason}")
            }
            Self::DuplicateTransaction(id) => write!(f, "transaction {} already exists", id.0),
            Self::UnknownTransaction(id) => write!(f, "transaction {} does not exist", id.0),
            Self::ActiveTransaction(id) => write!(
                f,
                "transaction {} is still active; finish or roll it back before releasing its history",
                id.0
            ),
            Self::InactiveTransaction { transaction, state } => {
                write!(f, "transaction {} is {state:?}, not active", transaction.0)
            }
            Self::Conflict {
                transaction,
                other,
                resource,
                kind,
                observed,
                actual,
            } => {
                write!(
                    f,
                    "transaction {} has a {kind:?} conflict on {resource} (observed {observed:?}, conflicting version {actual:?}",
                    transaction.0
                )?;
                if let Some(other) = other {
                    write!(f, ", published by transaction {}", other.0)?;
                }
                f.write_str("); retry from a fresh snapshot before publishing")
            }
            Self::CounterOverflow => f.write_str("transaction version counter exhausted"),
        }
    }
}

impl std::error::Error for StoreError {}

#[derive(Clone, Debug)]
struct Version<V> {
    epoch: Epoch,
    revision: u64,
    writer: Option<TransactionId>,
    value: Option<V>,
}

#[derive(Clone, Debug)]
struct Tentative<V> {
    revision: u64,
    value: Option<V>,
}

/// A transaction's state is owned by its store; callers hold only its stable ID.
#[derive(Clone, Debug)]
pub struct Transaction<V = Value> {
    id: TransactionId,
    isolation: Isolation,
    state: TransactionState,
    start: Epoch,
    statement: Epoch,
    pending: BTreeMap<ResourceKey, Tentative<V>>,
    write_bases: BTreeMap<ResourceKey, Option<Epoch>>,
    reads: BTreeMap<ResourceKey, Option<Epoch>>,
    predicates: BTreeMap<ResourcePrefix, Epoch>,
    origin: BTreeMap<ResourceKey, Option<V>>,
}

impl<V> Transaction<V> {
    pub fn id(&self) -> TransactionId {
        self.id
    }

    pub fn isolation(&self) -> Isolation {
        self.isolation
    }

    pub fn state(&self) -> TransactionState {
        self.state
    }

    pub fn start_epoch(&self) -> Epoch {
        self.start
    }

    pub fn pending_resources(&self) -> usize {
        self.pending.len()
    }

    pub fn origin_resources(&self) -> usize {
        self.origin.len()
    }

    fn read_epoch(&self) -> Epoch {
        match self.isolation {
            Isolation::ReadUncommitted | Isolation::ReadCommitted => self.statement,
            Isolation::RepeatableRead | Isolation::Serializable => self.start,
        }
    }

    fn clear_segment(&mut self) {
        self.pending.clear();
        self.write_bases.clear();
        self.reads.clear();
        self.predicates.clear();
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Retention {
    pub versions: usize,
    pub transactions: usize,
    pub origin_resources: usize,
    pub oldest_origin: Option<Epoch>,
}

/// Single-owner MVCC state with atomic logical publication.
///
/// All read/write methods borrow the store, so no partially staged payload or
/// partially published write set can be observed. Executors may protect the store
/// with a short-lived lock, but must perform payload I/O outside that lock.
/// Retained transactions pin version history for repeatable reads and named undo;
/// `forget` explicitly releases that ability when the planner proves it is dead.
/// `V` must denote an immutable version: the default is shared bytes, while a
/// filesystem adapter can use an owned artifact reference or tree manifest without
/// loading payloads into memory. Cloning a value must preserve that version.
#[derive(Clone, Debug)]
pub struct Store<V = Value> {
    epoch: Epoch,
    revision: u64,
    versions: BTreeMap<ResourceKey, Vec<Version<V>>>,
    transactions: BTreeMap<TransactionId, Transaction<V>>,
}

impl<V> Default for Store<V> {
    fn default() -> Self {
        Self {
            epoch: Epoch::default(),
            revision: 0,
            versions: BTreeMap::new(),
            transactions: BTreeMap::new(),
        }
    }
}

impl<V: Clone> Store<V> {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn epoch(&self) -> Epoch {
        self.epoch
    }

    /// The latest published value, without opening a transaction or recording a read.
    pub fn committed(&self, key: &ResourceKey) -> Option<V> {
        self.latest(key).and_then(|version| version.value.clone())
    }

    pub fn transaction(&self, id: TransactionId) -> Option<&Transaction<V>> {
        self.transactions.get(&id)
    }

    /// Install an adapter-observed initial value or an external publication.
    /// Existing snapshots remain valid and serializable validations see the change.
    pub fn seed(&mut self, key: ResourceKey, value: Option<V>) -> Result<Epoch, StoreError> {
        let (epoch, revision) = self.next_publication()?;
        self.append(key, value, None, epoch, revision);
        self.epoch = epoch;
        self.revision = revision;
        Ok(epoch)
    }

    pub fn begin(&mut self, id: TransactionId, isolation: Isolation) -> Result<(), StoreError> {
        if self.transactions.contains_key(&id) {
            return Err(StoreError::DuplicateTransaction(id));
        }
        self.transactions.insert(
            id,
            Transaction {
                id,
                isolation,
                state: TransactionState::Active,
                start: self.epoch,
                statement: self.epoch,
                pending: BTreeMap::new(),
                write_bases: BTreeMap::new(),
                reads: BTreeMap::new(),
                predicates: BTreeMap::new(),
                origin: BTreeMap::new(),
            },
        );
        Ok(())
    }

    /// Capture the read-committed statement snapshot before any operation in it.
    /// Read-uncommitted intentionally remains live within a statement.
    pub fn start_statement(&mut self, id: TransactionId) -> Result<Epoch, StoreError> {
        self.active(id)?;
        let epoch = self.epoch;
        self.transactions.get_mut(&id).unwrap().statement = epoch;
        Ok(epoch)
    }

    pub fn read(&mut self, id: TransactionId, key: &ResourceKey) -> Result<Option<V>, StoreError> {
        let transaction = self.active(id)?;
        if let Some(value) = transaction.pending.get(key) {
            return Ok(value.value.clone());
        }
        let version = self.visible_committed(transaction, key);
        let observed = version.map(|version| version.epoch);
        let value = if transaction.isolation == Isolation::ReadUncommitted {
            self.dirty_value(key)
        } else {
            version.and_then(|version| version.value.clone())
        };
        if transaction.isolation == Isolation::Serializable {
            self.transactions
                .get_mut(&id)
                .unwrap()
                .reads
                .entry(key.clone())
                .or_insert(observed);
        }
        Ok(value)
    }

    pub fn write(
        &mut self,
        id: TransactionId,
        key: ResourceKey,
        value: V,
    ) -> Result<(), StoreError> {
        self.stage(id, key, Some(value))
    }

    pub fn delete(&mut self, id: TransactionId, key: ResourceKey) -> Result<(), StoreError> {
        self.stage(id, key, None)
    }

    /// Enumerate an inclusive tree at one statement snapshot, recording even an
    /// empty predicate so serializable transactions detect insert/delete phantoms.
    pub fn enumerate(
        &mut self,
        id: TransactionId,
        prefix: &ResourcePrefix,
    ) -> Result<Vec<(ResourceKey, V)>, StoreError> {
        let transaction = self.active(id)?;
        let mut keys = self
            .versions
            .keys()
            .filter(|key| prefix.contains(key))
            .cloned()
            .collect::<BTreeSet<_>>();
        for other in self.transactions.values() {
            if other.id == id || transaction.isolation == Isolation::ReadUncommitted {
                keys.extend(
                    other
                        .pending
                        .keys()
                        .filter(|key| prefix.contains(key))
                        .cloned(),
                );
            }
        }
        let epoch = transaction.read_epoch();
        let serializable = transaction.isolation == Isolation::Serializable;
        let mut result = Vec::new();
        for key in keys {
            if let Some(value) = self.read(id, &key)? {
                result.push((key, value));
            }
        }
        if serializable {
            self.transactions
                .get_mut(&id)
                .unwrap()
                .predicates
                .entry(prefix.clone())
                .or_insert(epoch);
        }
        Ok(result)
    }

    /// Validate the whole segment, then publish all staged values at one epoch.
    /// On any conflict neither pending state nor committed versions are changed.
    pub fn checkpoint(&mut self, id: TransactionId) -> Result<Epoch, StoreError> {
        let transaction = self.active(id)?;
        self.validate(transaction)?;
        if transaction.pending.is_empty() {
            self.transactions.get_mut(&id).unwrap().clear_segment();
            return Ok(self.epoch);
        }
        let (epoch, revision) = self.next_publication()?;
        let origin = transaction
            .pending
            .keys()
            .filter(|key| !transaction.origin.contains_key(*key))
            .map(|key| {
                let value = self
                    .at_epoch(key, transaction.start)
                    .and_then(|version| version.value.clone());
                (key.clone(), value)
            })
            .collect::<Vec<_>>();
        let transaction = self.transactions.get_mut(&id).unwrap();
        transaction.origin.extend(origin);
        let pending = std::mem::take(&mut transaction.pending);
        transaction.clear_segment();
        for (key, value) in pending {
            self.append(key, value.value, Some(id), epoch, revision);
        }
        self.epoch = epoch;
        self.revision = revision;
        Ok(epoch)
    }

    /// Discard only changes and observations since the latest successful checkpoint.
    pub fn rollback_pending(&mut self, id: TransactionId) -> Result<(), StoreError> {
        self.active(id)?;
        self.transactions.get_mut(&id).unwrap().clear_segment();
        Ok(())
    }

    /// The implicit commit at a transaction block's end. Its origin remains named.
    pub fn finish(&mut self, id: TransactionId) -> Result<Epoch, StoreError> {
        let epoch = self.checkpoint(id)?;
        self.transactions.get_mut(&id).unwrap().state = TransactionState::Finished;
        Ok(epoch)
    }

    /// Restore the transaction's beginning, including all prior checkpoints.
    ///
    /// The planner must retain conflicting resource dependencies through a named
    /// rollback. A foreign publication on a touched key after this transaction
    /// began makes compensation unsafe and is rejected, never overwritten.
    pub fn rollback_origin(&mut self, id: TransactionId) -> Result<Epoch, StoreError> {
        let transaction = self
            .transactions
            .get(&id)
            .ok_or(StoreError::UnknownTransaction(id))?;
        if transaction.state == TransactionState::RolledBack {
            return Err(StoreError::InactiveTransaction {
                transaction: id,
                state: transaction.state,
            });
        }
        for key in transaction.origin.keys() {
            if let Some(version) = self.versions.get(key).and_then(|chain| {
                chain
                    .iter()
                    .find(|version| version.epoch > transaction.start && version.writer != Some(id))
            }) {
                return Err(self.conflict(
                    transaction,
                    key,
                    ConflictKind::Origin,
                    Some(transaction.start),
                    Some(version),
                ));
            }
        }
        if transaction.origin.is_empty() {
            let transaction = self.transactions.get_mut(&id).unwrap();
            transaction.clear_segment();
            transaction.state = TransactionState::RolledBack;
            return Ok(self.epoch);
        }
        let (epoch, revision) = self.next_publication()?;
        let origin = transaction.origin.clone();
        for (key, value) in origin {
            self.append(key, value, Some(id), epoch, revision);
        }
        let transaction = self.transactions.get_mut(&id).unwrap();
        transaction.clear_segment();
        transaction.state = TransactionState::RolledBack;
        self.epoch = epoch;
        self.revision = revision;
        Ok(epoch)
    }

    /// Release an ended transaction's snapshots and named rollback capability.
    /// History is retained here; adapters can inspect `retention` before choosing
    /// a session boundary. This avoids unsafe GC while other origin journals live.
    pub fn forget(&mut self, id: TransactionId) -> Result<(), StoreError> {
        let transaction = self
            .transactions
            .get(&id)
            .ok_or(StoreError::UnknownTransaction(id))?;
        if transaction.state == TransactionState::Active {
            return Err(StoreError::ActiveTransaction(id));
        }
        self.transactions.remove(&id);
        Ok(())
    }

    pub fn retention(&self) -> Retention {
        Retention {
            versions: self.versions.values().map(Vec::len).sum(),
            transactions: self.transactions.len(),
            origin_resources: self
                .transactions
                .values()
                .map(|transaction| transaction.origin.len())
                .sum(),
            oldest_origin: self
                .transactions
                .values()
                .map(|transaction| transaction.start)
                .min(),
        }
    }

    fn active(&self, id: TransactionId) -> Result<&Transaction<V>, StoreError> {
        let transaction = self
            .transactions
            .get(&id)
            .ok_or(StoreError::UnknownTransaction(id))?;
        if transaction.state != TransactionState::Active {
            return Err(StoreError::InactiveTransaction {
                transaction: id,
                state: transaction.state,
            });
        }
        Ok(transaction)
    }

    fn stage(
        &mut self,
        id: TransactionId,
        key: ResourceKey,
        value: Option<V>,
    ) -> Result<(), StoreError> {
        let transaction = self.active(id)?;
        let base = self
            .visible_committed(transaction, &key)
            .map(|version| version.epoch);
        let revision = self
            .revision
            .checked_add(1)
            .ok_or(StoreError::CounterOverflow)?;
        let transaction = self.transactions.get_mut(&id).unwrap();
        transaction.write_bases.entry(key.clone()).or_insert(base);
        transaction
            .pending
            .insert(key, Tentative { revision, value });
        self.revision = revision;
        Ok(())
    }

    fn visible_committed<'a>(
        &'a self,
        transaction: &Transaction<V>,
        key: &ResourceKey,
    ) -> Option<&'a Version<V>> {
        self.versions.get(key)?.iter().rev().find(|version| {
            version.epoch <= transaction.read_epoch() || version.writer == Some(transaction.id)
        })
    }

    fn at_epoch(&self, key: &ResourceKey, epoch: Epoch) -> Option<&Version<V>> {
        self.versions
            .get(key)?
            .iter()
            .rev()
            .find(|version| version.epoch <= epoch)
    }

    fn latest(&self, key: &ResourceKey) -> Option<&Version<V>> {
        self.versions.get(key).and_then(|chain| chain.last())
    }

    fn dirty_value(&self, key: &ResourceKey) -> Option<V> {
        let committed = self.latest(key);
        let tentative = self
            .transactions
            .values()
            .filter(|transaction| transaction.state == TransactionState::Active)
            .filter_map(|transaction| transaction.pending.get(key))
            .max_by_key(|value| value.revision);
        match tentative {
            Some(value) if committed.is_none_or(|version| value.revision > version.revision) => {
                value.value.clone()
            }
            _ => committed.and_then(|version| version.value.clone()),
        }
    }

    fn validate(&self, transaction: &Transaction<V>) -> Result<(), StoreError> {
        for (key, observed) in &transaction.write_bases {
            let latest = self.latest(key);
            if latest.map(|version| version.epoch) != *observed {
                return Err(self.conflict(
                    transaction,
                    key,
                    ConflictKind::Write,
                    *observed,
                    latest,
                ));
            }
        }
        if transaction.isolation != Isolation::Serializable {
            return Ok(());
        }
        for (key, observed) in &transaction.reads {
            let latest = self.latest(key);
            if latest.map(|version| version.epoch) != *observed {
                return Err(self.conflict(transaction, key, ConflictKind::Read, *observed, latest));
            }
        }
        for (prefix, observed) in &transaction.predicates {
            for (key, chain) in self.versions.iter().filter(|(key, _)| prefix.contains(key)) {
                if let Some(version) = chain.iter().find(|version| {
                    version.epoch > *observed && version.writer != Some(transaction.id)
                }) {
                    return Err(self.conflict(
                        transaction,
                        key,
                        ConflictKind::Predicate,
                        Some(*observed),
                        Some(version),
                    ));
                }
            }
        }
        Ok(())
    }

    fn conflict(
        &self,
        transaction: &Transaction<V>,
        key: &ResourceKey,
        kind: ConflictKind,
        observed: Option<Epoch>,
        actual: Option<&Version<V>>,
    ) -> StoreError {
        StoreError::Conflict {
            transaction: transaction.id,
            other: actual.and_then(|version| version.writer),
            resource: key.clone(),
            kind,
            observed,
            actual: actual.map(|version| version.epoch),
        }
    }

    fn next_publication(&self) -> Result<(Epoch, u64), StoreError> {
        let epoch = self
            .epoch
            .0
            .checked_add(1)
            .ok_or(StoreError::CounterOverflow)?;
        let revision = self
            .revision
            .checked_add(1)
            .ok_or(StoreError::CounterOverflow)?;
        Ok((Epoch(epoch), revision))
    }

    fn append(
        &mut self,
        key: ResourceKey,
        value: Option<V>,
        writer: Option<TransactionId>,
        epoch: Epoch,
        revision: u64,
    ) {
        self.versions.entry(key).or_default().push(Version {
            epoch,
            revision,
            writer,
            value,
        });
    }
}
