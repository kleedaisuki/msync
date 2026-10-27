//! Transactional resource versions, independent of filesystem and process adapters.
//!
//! A checkpoint publishes one complete write set, but retains the transaction's
//! origin journal for a later named rollback. Publication here changes the logical
//! resource store only; an adapter must mediate external readers and publish files
//! before it can claim the same isolation outside this store.

pub mod filesystem;
mod store;

pub use store::{
    ConflictKind, Epoch, ResourceKey, ResourcePrefix, Retention, Store, StoreError, Transaction,
    TransactionState, Value,
};
