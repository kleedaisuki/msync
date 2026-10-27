use std::sync::Arc;

use msync::hir::TransactionId;
use msync::language::Isolation;
use msync::transaction::{ConflictKind, ResourceKey, Store, StoreError, TransactionState, Value};
use proptest::prelude::*;

fn key(path: &str) -> ResourceKey {
    ResourceKey::new("local:volume:test", path).unwrap()
}

fn value(text: &str) -> Value {
    Arc::from(text.as_bytes())
}

fn read(store: &mut Store, id: TransactionId, path: &str) -> Option<String> {
    store
        .read(id, &key(path))
        .unwrap()
        .map(|bytes| String::from_utf8(bytes.to_vec()).unwrap())
}

fn seed(store: &mut Store, path: &str, text: &str) {
    store.seed(key(path), Some(value(text))).unwrap();
}

#[test]
fn resource_identity_normalizes_components_without_prefix_aliases() {
    let root = key("/one/../two/./");
    assert_eq!(root, key("/two"));
    assert!(root.tree().contains(&key("/two/文件")));
    assert!(root.tree().contains(&root));
    assert!(!root.tree().contains(&key("/two-more/文件")));
    assert!(
        !root
            .tree()
            .contains(&ResourceKey::new("ssh:other", "/two/file").unwrap())
    );
    assert!(ResourceKey::new("local", "relative").is_err());
    assert!(ResourceKey::new("local", "/../../escape").is_err());
    assert!(ResourceKey::new("local", "/nul\0file").is_err());
}

#[test]
fn uncommitted_reads_see_complete_tentatives_but_not_rolled_back_values() {
    let mut store = Store::new();
    seed(&mut store, "/file", "old");
    let writer = TransactionId(0);
    let dirty = TransactionId(1);
    let committed = TransactionId(2);
    store.begin(writer, Isolation::ReadCommitted).unwrap();
    store.begin(dirty, Isolation::ReadUncommitted).unwrap();
    store.begin(committed, Isolation::ReadCommitted).unwrap();
    store
        .write(writer, key("/file"), value("whole tentative payload"))
        .unwrap();
    assert_eq!(
        read(&mut store, dirty, "/file").as_deref(),
        Some("whole tentative payload")
    );
    assert_eq!(read(&mut store, committed, "/file").as_deref(), Some("old"));
    store.rollback_pending(writer).unwrap();
    assert_eq!(read(&mut store, dirty, "/file").as_deref(), Some("old"));
    store.delete(writer, key("/file")).unwrap();
    assert_eq!(read(&mut store, dirty, "/file"), None);
    store.finish(writer).unwrap();
    store.start_statement(committed).unwrap();
    assert_eq!(read(&mut store, committed, "/file"), None);
}

#[test]
fn an_older_tentative_cannot_hide_a_later_completed_publication_from_dirty_readers() {
    let mut store = Store::new();
    seed(&mut store, "/file", "old");
    let stale = TransactionId(0);
    let current = TransactionId(1);
    let reader = TransactionId(2);
    store.begin(stale, Isolation::ReadCommitted).unwrap();
    store.begin(current, Isolation::ReadCommitted).unwrap();
    store.begin(reader, Isolation::ReadUncommitted).unwrap();
    store
        .write(stale, key("/file"), value("older tentative"))
        .unwrap();
    store
        .write(current, key("/file"), value("later publication"))
        .unwrap();
    store.finish(current).unwrap();
    assert_eq!(
        read(&mut store, reader, "/file").as_deref(),
        Some("later publication")
    );
    assert!(matches!(
        store.finish(stale),
        Err(StoreError::Conflict {
            kind: ConflictKind::Write,
            ..
        })
    ));
}

#[test]
fn read_committed_is_stable_within_statement_but_refreshes_between_statements() {
    let mut store = Store::new();
    seed(&mut store, "/file", "old");
    let reader = TransactionId(0);
    let writer = TransactionId(1);
    store.begin(reader, Isolation::ReadCommitted).unwrap();
    store.begin(writer, Isolation::Serializable).unwrap();
    store.start_statement(reader).unwrap();
    assert_eq!(read(&mut store, reader, "/file").as_deref(), Some("old"));
    store.write(writer, key("/file"), value("new")).unwrap();
    store.finish(writer).unwrap();
    assert_eq!(read(&mut store, reader, "/file").as_deref(), Some("old"));
    store.start_statement(reader).unwrap();
    assert_eq!(read(&mut store, reader, "/file").as_deref(), Some("new"));
}

#[test]
fn repeatable_read_retains_begin_snapshot_and_reads_own_checkpoints() {
    let mut store = Store::new();
    seed(&mut store, "/file", "old");
    let reader = TransactionId(0);
    let writer = TransactionId(1);
    store.begin(reader, Isolation::RepeatableRead).unwrap();
    store.begin(writer, Isolation::Serializable).unwrap();
    store.write(reader, key("/own"), value("mine")).unwrap();
    store.checkpoint(reader).unwrap();
    store.write(writer, key("/file"), value("new")).unwrap();
    store.finish(writer).unwrap();
    store.start_statement(reader).unwrap();
    assert_eq!(read(&mut store, reader, "/file").as_deref(), Some("old"));
    assert_eq!(read(&mut store, reader, "/own").as_deref(), Some("mine"));
}

fn write_skew(isolation: Isolation) -> Result<(), StoreError> {
    let mut store = Store::new();
    seed(&mut store, "/doctor/alice", "on");
    seed(&mut store, "/doctor/bob", "on");
    let alice = TransactionId(0);
    let bob = TransactionId(1);
    store.begin(alice, isolation).unwrap();
    store.begin(bob, isolation).unwrap();
    assert_eq!(
        read(&mut store, alice, "/doctor/bob").as_deref(),
        Some("on")
    );
    assert_eq!(
        read(&mut store, bob, "/doctor/alice").as_deref(),
        Some("on")
    );
    store
        .write(alice, key("/doctor/alice"), value("off"))
        .unwrap();
    store.write(bob, key("/doctor/bob"), value("off")).unwrap();
    store.finish(alice)?;
    store.finish(bob)?;
    Ok(())
}

#[test]
fn serializable_prevents_write_skew_that_repeatable_read_allows() {
    write_skew(Isolation::RepeatableRead).unwrap();
    assert!(matches!(
        write_skew(Isolation::Serializable),
        Err(StoreError::Conflict {
            kind: ConflictKind::Read,
            other: Some(TransactionId(0)),
            ..
        })
    ));
}

#[test]
fn absent_key_reads_and_empty_tree_predicates_are_dependencies() {
    let mut store = Store::new();
    let absent = TransactionId(0);
    let predicate = TransactionId(1);
    let writer = TransactionId(2);
    store.begin(absent, Isolation::Serializable).unwrap();
    store.begin(predicate, Isolation::Serializable).unwrap();
    store.begin(writer, Isolation::Serializable).unwrap();
    assert_eq!(read(&mut store, absent, "/tree/new"), None);
    assert!(
        store
            .enumerate(predicate, &key("/tree").tree())
            .unwrap()
            .is_empty()
    );
    store
        .write(writer, key("/tree/new"), value("new child"))
        .unwrap();
    store.finish(writer).unwrap();
    assert!(matches!(
        store.finish(absent),
        Err(StoreError::Conflict {
            kind: ConflictKind::Read,
            ..
        })
    ));
    assert!(matches!(
        store.finish(predicate),
        Err(StoreError::Conflict {
            kind: ConflictKind::Predicate,
            ..
        })
    ));
}

#[test]
fn tree_predicates_do_not_conflict_with_other_namespaces_or_component_prefixes() {
    let mut store = Store::new();
    let reader = TransactionId(0);
    let writer = TransactionId(1);
    store.begin(reader, Isolation::Serializable).unwrap();
    store.begin(writer, Isolation::Serializable).unwrap();
    store.enumerate(reader, &key("/tree").tree()).unwrap();
    store
        .write(writer, key("/treehouse/file"), value("outside"))
        .unwrap();
    store
        .write(
            writer,
            ResourceKey::new("ssh:other", "/tree/file").unwrap(),
            value("remote"),
        )
        .unwrap();
    store.finish(writer).unwrap();
    store.finish(reader).unwrap();
}

#[test]
fn a_phantom_insert_then_delete_still_invalidates_an_empty_tree_read() {
    let mut store = Store::new();
    let reader = TransactionId(0);
    let writer = TransactionId(1);
    store.begin(reader, Isolation::Serializable).unwrap();
    store.begin(writer, Isolation::Serializable).unwrap();
    store.enumerate(reader, &key("/tree").tree()).unwrap();
    store
        .write(writer, key("/tree/temporary"), value("new"))
        .unwrap();
    store.checkpoint(writer).unwrap();
    store.delete(writer, key("/tree/temporary")).unwrap();
    store.finish(writer).unwrap();
    assert!(store.committed(&key("/tree/temporary")).is_none());
    assert!(matches!(
        store.finish(reader),
        Err(StoreError::Conflict {
            kind: ConflictKind::Predicate,
            ..
        })
    ));
}

#[test]
fn checkpoint_and_full_rollback_have_distinct_scopes_and_preserve_unrelated_writes() {
    let mut store = Store::new();
    seed(&mut store, "/existing", "before");
    let a = TransactionId(0);
    let b = TransactionId(1);
    let observer = TransactionId(2);
    store.begin(a, Isolation::Serializable).unwrap();
    store
        .write(a, key("/existing"), value("checkpoint1"))
        .unwrap();
    store.write(a, key("/created"), value("created")).unwrap();
    store.checkpoint(a).unwrap();
    store
        .write(a, key("/existing"), value("checkpoint2"))
        .unwrap();
    store.checkpoint(a).unwrap();
    store
        .write(a, key("/existing"), value("discarded"))
        .unwrap();
    store.delete(a, key("/created")).unwrap();
    store.rollback_pending(a).unwrap();
    assert_eq!(
        read(&mut store, a, "/existing").as_deref(),
        Some("checkpoint2")
    );
    assert_eq!(read(&mut store, a, "/created").as_deref(), Some("created"));
    store.finish(a).unwrap();
    store.begin(b, Isolation::Serializable).unwrap();
    store
        .write(b, key("/unrelated"), value("retained"))
        .unwrap();
    store.finish(b).unwrap();
    store.rollback_origin(a).unwrap();
    assert_eq!(
        store.transaction(a).unwrap().state(),
        TransactionState::RolledBack
    );
    store.begin(observer, Isolation::ReadCommitted).unwrap();
    assert_eq!(
        read(&mut store, observer, "/existing").as_deref(),
        Some("before")
    );
    assert_eq!(read(&mut store, observer, "/created"), None);
    assert_eq!(
        read(&mut store, observer, "/unrelated").as_deref(),
        Some("retained")
    );
}

#[test]
fn origin_rollback_never_erases_an_intervening_foreign_publication() {
    let mut store = Store::new();
    seed(&mut store, "/file", "initial");
    let a = TransactionId(0);
    let b = TransactionId(1);
    store.begin(a, Isolation::ReadCommitted).unwrap();
    store.write(a, key("/file"), value("a")).unwrap();
    store.checkpoint(a).unwrap();
    store.begin(b, Isolation::Serializable).unwrap();
    store.write(b, key("/file"), value("b")).unwrap();
    store.finish(b).unwrap();
    store.start_statement(a).unwrap();
    store.write(a, key("/file"), value("a later")).unwrap();
    store.checkpoint(a).unwrap();
    let before = store.epoch();
    assert!(matches!(
        store.rollback_origin(a),
        Err(StoreError::Conflict {
            kind: ConflictKind::Origin,
            other: Some(TransactionId(1)),
            ..
        })
    ));
    assert_eq!(store.epoch(), before);
    assert_eq!(read(&mut store, a, "/file").as_deref(), Some("a later"));
}

#[test]
fn origin_rollback_protects_a_foreign_version_from_before_the_first_own_checkpoint() {
    let mut store = Store::new();
    seed(&mut store, "/file", "initial");
    let a = TransactionId(0);
    let b = TransactionId(1);
    store.begin(a, Isolation::ReadCommitted).unwrap();
    store.begin(b, Isolation::ReadCommitted).unwrap();
    store
        .write(b, key("/file"), value("foreign update"))
        .unwrap();
    store.finish(b).unwrap();
    store.start_statement(a).unwrap();
    store.write(a, key("/file"), value("own update")).unwrap();
    store.finish(a).unwrap();
    assert!(matches!(
        store.rollback_origin(a),
        Err(StoreError::Conflict {
            kind: ConflictKind::Origin,
            other: Some(TransactionId(1)),
            ..
        })
    ));
    assert_eq!(
        store.committed(&key("/file")).unwrap().as_ref(),
        b"own update"
    );
}

#[test]
fn publication_conflict_never_partially_commits_other_keys_or_discards_pending() {
    let mut store = Store::new();
    seed(&mut store, "/shared", "initial");
    let a = TransactionId(0);
    let b = TransactionId(1);
    let observer = TransactionId(2);
    store.begin(a, Isolation::RepeatableRead).unwrap();
    store.begin(b, Isolation::RepeatableRead).unwrap();
    store.write(a, key("/shared"), value("a")).unwrap();
    store
        .write(a, key("/other"), value("must not publish"))
        .unwrap();
    store.write(b, key("/shared"), value("b")).unwrap();
    store.finish(b).unwrap();
    let before = store.epoch();
    assert!(matches!(
        store.checkpoint(a),
        Err(StoreError::Conflict {
            kind: ConflictKind::Write,
            ..
        })
    ));
    assert_eq!(store.epoch(), before);
    assert_eq!(store.transaction(a).unwrap().pending_resources(), 2);
    store.begin(observer, Isolation::ReadCommitted).unwrap();
    assert_eq!(read(&mut store, observer, "/shared").as_deref(), Some("b"));
    assert_eq!(read(&mut store, observer, "/other"), None);
    store.rollback_pending(a).unwrap();
    assert_eq!(store.transaction(a).unwrap().pending_resources(), 0);
}

#[test]
fn a_successful_checkpoint_publishes_every_key_at_one_epoch() {
    let mut store = Store::new();
    let writer = TransactionId(0);
    let before = TransactionId(1);
    let after = TransactionId(2);
    store.begin(writer, Isolation::Serializable).unwrap();
    store.begin(before, Isolation::RepeatableRead).unwrap();
    store.write(writer, key("/a"), value("a")).unwrap();
    store.write(writer, key("/b"), value("b")).unwrap();
    let initial = store.epoch();
    assert_eq!(store.checkpoint(writer).unwrap().0, initial.0 + 1);
    store.begin(after, Isolation::RepeatableRead).unwrap();
    assert_eq!(read(&mut store, before, "/a"), None);
    assert_eq!(read(&mut store, before, "/b"), None);
    assert_eq!(read(&mut store, after, "/a").as_deref(), Some("a"));
    assert_eq!(read(&mut store, after, "/b").as_deref(), Some("b"));
}

#[test]
fn identities_states_and_retention_are_explicit() {
    let mut store = Store::new();
    let id = TransactionId(15);
    assert!(matches!(
        store.start_statement(id),
        Err(StoreError::UnknownTransaction(_))
    ));
    store.begin(id, Isolation::Serializable).unwrap();
    assert_eq!(store.transaction(id).unwrap().id(), id);
    assert_eq!(
        store.transaction(id).unwrap().isolation(),
        Isolation::Serializable
    );
    assert!(matches!(
        store.begin(id, Isolation::Serializable),
        Err(StoreError::DuplicateTransaction(_))
    ));
    store.write(id, key("/one"), value("one")).unwrap();
    store.finish(id).unwrap();
    assert!(matches!(
        store.read(id, &key("/one")),
        Err(StoreError::InactiveTransaction { .. })
    ));
    assert_eq!(store.retention().origin_resources, 1);
    store.forget(id).unwrap();
    assert_eq!(store.retention().transactions, 0);
    assert_eq!(store.retention().versions, 1);
    assert!(matches!(
        store.rollback_origin(id),
        Err(StoreError::UnknownTransaction(_))
    ));
}

#[test]
fn versions_can_reference_immutable_disk_artifacts_without_loading_payloads() {
    #[derive(Clone, Debug, PartialEq, Eq)]
    struct Artifact {
        object_id: u64,
        bytes: u64,
    }

    let mut store = Store::<Artifact>::new();
    let old = Artifact {
        object_id: 10,
        bytes: 1 << 40,
    };
    let new = Artifact {
        object_id: 11,
        bytes: 1 << 40,
    };
    let resource = key("/terabyte-file");
    store.seed(resource.clone(), Some(old.clone())).unwrap();
    let id = TransactionId(0);
    store.begin(id, Isolation::Serializable).unwrap();
    store.write(id, resource.clone(), new.clone()).unwrap();
    store.finish(id).unwrap();
    assert_eq!(store.committed(&resource), Some(new));
    store.rollback_origin(id).unwrap();
    assert_eq!(store.committed(&resource), Some(old));
}

proptest! {
    #[test]
    fn arbitrary_checkpoints_restore_original_bytes_and_absence(
        original in prop::collection::vec(any::<u8>(), 0..100),
        stages in prop::collection::vec(prop::collection::vec(any::<u8>(), 0..100), 0..12),
    ) {
        let mut store = Store::<Value>::new();
        let old = key("/old");
        let new = key("/new");
        store.seed(old.clone(), Some(Arc::from(original.clone()))).unwrap();
        let id = TransactionId(0);
        store.begin(id, Isolation::Serializable).unwrap();
        for stage in stages {
            store.write(id, old.clone(), Arc::from(stage.clone())).unwrap();
            store.write(id, new.clone(), Arc::from(stage)).unwrap();
            store.checkpoint(id).unwrap();
        }
        store.finish(id).unwrap();
        store.rollback_origin(id).unwrap();
        let observer = TransactionId(1);
        store.begin(observer, Isolation::ReadCommitted).unwrap();
        let restored = store.read(observer, &old).unwrap().unwrap();
        prop_assert_eq!(restored.as_ref(), original.as_slice());
        prop_assert!(store.read(observer, &new).unwrap().is_none());
    }
}
