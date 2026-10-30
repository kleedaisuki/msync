use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use msync::filesystem::SnapshotStore;
use msync::filesystem::durable::Batch;
use msync::filesystem::recovery::{self, Registry};

fn workspace() -> tempfile::TempDir {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    tempfile::Builder::new()
        .prefix("recovery-registry-v5-")
        .tempdir_in(temporary)
        .unwrap()
}

fn batch(root: &Path, name: &str) -> (Batch, PathBuf) {
    let parent = root.join(name);
    fs::create_dir_all(&parent).unwrap();
    let source = root.join(format!("{name}-source"));
    let target = parent.join("target");
    fs::write(&source, b"complete new value").unwrap();
    fs::write(&target, b"complete old value").unwrap();
    let store = SnapshotStore::new(&root.join(format!("{name}-assets"))).unwrap();
    let snapshot = store.capture(&source).unwrap();
    let publication = store.prepare(&snapshot, &target).unwrap();
    (Batch::prepare(vec![publication]).unwrap(), target)
}

#[test]
fn missing_registry_recovery_does_not_create_scratch_or_probe_destinations() {
    let root = workspace();
    let scratch = root.path().join("absent-scratch");
    assert!(recovery::recover(&scratch).unwrap().is_empty());
    assert!(!scratch.exists());
}

#[test]
fn opening_a_missing_registry_does_not_create_control_state() {
    let root = workspace();
    let scratch = root.path().join("absent-scratch");
    assert!(Registry::open_existing(&scratch).unwrap().is_none());
    assert!(!scratch.exists());
    fs::create_dir(&scratch).unwrap();
    assert!(Registry::open_existing(&scratch).unwrap().is_none());
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 0);
}

#[test]
fn opening_an_existing_registry_excludes_live_records_without_reading_them() {
    let root = workspace();
    let source = root.path().join("source");
    let scratch = source.join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let stage = scratch.join("execute-live-owner");
    fs::create_dir(&stage).unwrap();
    let registration = registry
        .enter()
        .unwrap()
        .register_stage(&scratch, &stage)
        .unwrap();
    let before = fs::metadata(registry.directory())
        .unwrap()
        .modified()
        .unwrap();
    let existing = Registry::open_existing(&scratch).unwrap().unwrap();
    assert!(same_file::is_same_file(existing.directory(), registry.directory()).unwrap());
    assert_eq!(
        fs::metadata(existing.directory())
            .unwrap()
            .modified()
            .unwrap(),
        before
    );
    let mut store = SnapshotStore::new(&root.path().join("assets")).unwrap();
    store.exclude_registry(&existing).unwrap();
    let snapshot = store.capture(&source).unwrap();
    assert!(
        snapshot
            .entries()
            .keys()
            .all(|path| !path.starts_with("scratch/.msync-recovery-v1"))
    );
    drop(snapshot);
    registration.finish().unwrap();
}

#[test]
fn opening_an_uninitialized_directory_does_not_create_its_missing_gate() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let gate = registry.directory().join(".lock");
    fs::remove_file(&gate).unwrap();
    assert!(Registry::open_existing(&scratch).is_err());
    assert!(!gate.exists());
}

#[test]
fn publication_lease_does_not_lock_out_unrelated_recovery() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    assert!(recovery::recover(&scratch).unwrap().is_empty());
    let (mut batch, target) = batch(root.path(), "destination");
    let registration = lease
        .register(batch.parent(), batch.journal_path())
        .unwrap();
    assert!(recovery::recover(&scratch).unwrap().is_empty());
    batch.publish().unwrap();
    batch.finalize().unwrap();
    registration.finish().unwrap();
    drop(batch);
    assert!(recovery::recover(&scratch).unwrap().is_empty());
    assert_eq!(fs::read(target).unwrap(), b"complete new value");
}

#[test]
fn unpromoted_owned_stage_is_recovered_without_changing_its_destination() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let parent = root.path().join("destination");
    fs::create_dir(&parent).unwrap();
    let target = parent.join("target");
    fs::write(&target, b"old target").unwrap();
    let stage = parent.join(".msync-publication-stage-test");
    fs::create_dir(&stage).unwrap();
    let registration = lease.register_stage(&parent, &stage).unwrap();
    fs::create_dir(stage.join("new")).unwrap();
    fs::write(stage.join("new/large-payload"), vec![7; 256 * 1024]).unwrap();
    drop(lease);
    assert!(recovery::recover(&scratch).unwrap().is_empty());
    drop(registration);

    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].journal, stage);
    assert!(!stage.exists());
    assert_eq!(fs::read(target).unwrap(), b"old target");
}

#[test]
fn outer_invocation_stage_reclaims_captured_sources_imports_and_spools() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let invocation = scratch.join("execute-owned-worker-test");
    let user = scratch.join("execute-user-data");
    fs::create_dir(&invocation).unwrap();
    fs::create_dir(&user).unwrap();
    fs::write(user.join("keep"), b"unregistered user data").unwrap();
    let registration = lease.register_stage(&scratch, &invocation).unwrap();
    for directory in ["assets", "imports", "spools"] {
        fs::create_dir(invocation.join(directory)).unwrap();
        fs::write(
            invocation.join(directory).join("payload"),
            vec![5; 64 * 1024],
        )
        .unwrap();
    }
    drop(lease);
    drop(registration);

    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].journal, invocation);
    assert!(!invocation.exists());
    assert_eq!(
        fs::read(user.join("keep")).unwrap(),
        b"unregistered user data"
    );
}

#[test]
fn live_outer_owner_does_not_block_an_unrelated_dead_stage_recovery() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let live = scratch.join("execute-live-owner");
    fs::create_dir(&live).unwrap();
    let live_lease = registry.enter().unwrap();
    let live_owner = live_lease.register_stage(&scratch, &live).unwrap();
    fs::write(live.join("spool"), b"still running").unwrap();

    let parent = root.path().join("unrelated-destination");
    fs::create_dir(&parent).unwrap();
    let dead = parent.join(".msync-publication-dead-owner");
    fs::create_dir(&dead).unwrap();
    let other_registry = Registry::new(&scratch).unwrap();
    let dead_owner = other_registry
        .enter()
        .unwrap()
        .register_stage(&parent, &dead)
        .unwrap();
    fs::write(dead.join("payload"), b"interrupted copy").unwrap();
    drop(dead_owner);

    let gate = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(registry.directory().join(".lock"))
        .unwrap();
    gate.try_lock()
        .expect("live payload ownership must not retain an index lock");
    drop(gate);
    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].journal, dead);
    assert!(!dead.exists());
    assert_eq!(fs::read(live.join("spool")).unwrap(), b"still running");
    assert!(recovery::recover(&scratch).unwrap().is_empty());

    fs::remove_dir_all(&live).unwrap();
    live_owner.finish().unwrap();
    drop(live_lease);
}

#[test]
fn coordinator_recovery_precedes_its_residual_stage_registrations() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let parent = root.path().join("destination");
    fs::create_dir(&parent).unwrap();
    let source = root.path().join("source");
    let target = parent.join("target");
    fs::write(&source, b"new target").unwrap();
    fs::write(&target, b"old target").unwrap();
    let store = SnapshotStore::new(&root.path().join("assets")).unwrap();
    let snapshot = store.capture(&source).unwrap();
    let (publication, publication_stage) = store
        .prepare_registered(&snapshot, &target, &lease)
        .unwrap();
    let publication_journal = publication.journal().to_owned();
    let (mut batch, coordinator_stage) =
        Batch::prepare_registered(vec![publication], &lease).unwrap();
    let coordinator_journal = batch.journal_path().to_owned();
    let coordinator = lease
        .register(batch.parent(), batch.journal_path())
        .unwrap();
    batch.publish().unwrap();
    drop(batch);
    drop(coordinator);
    drop(publication_stage);
    drop(coordinator_stage);

    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 3);
    assert_eq!(reports[0].journal, coordinator_journal);
    assert_eq!(reports[0].recovered, 1);
    assert_eq!(reports[1].recovered + reports[2].recovered, 0);
    assert!(!publication_journal.exists());
    assert!(!coordinator_journal.exists());
    assert_eq!(fs::read(target).unwrap(), b"new target");
}

#[test]
fn stage_recovery_retains_a_directory_with_a_changed_marker() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let stage = root.path().join(".msync-publication-marker-test");
    fs::create_dir(&stage).unwrap();
    let registration = lease.register_stage(root.path(), &stage).unwrap();
    fs::write(
        stage.join(".msync-owned-stage-v1"),
        b"not the registered token",
    )
    .unwrap();
    fs::write(stage.join("payload"), b"must remain for diagnosis").unwrap();
    drop(lease);
    drop(registration);

    assert!(recovery::recover(&scratch).is_err());
    assert_eq!(
        fs::read(stage.join("payload")).unwrap(),
        b"must remain for diagnosis"
    );
}

#[test]
fn stage_recovery_does_not_treat_a_copied_marker_as_directory_ownership() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let stage = root.path().join(".msync-publication-identity-test");
    fs::create_dir(&stage).unwrap();
    let registration = lease.register_stage(root.path(), &stage).unwrap();
    let token = fs::read(stage.join(".msync-owned-stage-v1")).unwrap();
    drop(lease);
    drop(registration);
    fs::rename(&stage, root.path().join("original-stage")).unwrap();
    fs::create_dir(&stage).unwrap();
    fs::write(stage.join(".msync-owned-stage-v1"), token).unwrap();
    fs::write(stage.join("foreign"), b"unrelated user directory").unwrap();

    assert!(recovery::recover(&scratch).is_err());
    assert_eq!(
        fs::read(stage.join("foreign")).unwrap(),
        b"unrelated user directory"
    );
}

#[test]
fn stage_registration_rejects_user_payload_and_non_owned_names() {
    let root = workspace();
    let registry = Registry::new(&root.path().join("scratch")).unwrap();
    let lease = registry.enter().unwrap();
    let ordinary = root.path().join("user-directory");
    let occupied = root.path().join(".msync-publication-occupied");
    fs::create_dir(&ordinary).unwrap();
    fs::create_dir(&occupied).unwrap();
    fs::write(occupied.join("keep"), b"user value").unwrap();

    assert!(lease.register_stage(root.path(), &ordinary).is_err());
    assert!(lease.register_stage(root.path(), &occupied).is_err());
    assert!(!ordinary.join(".msync-owned-stage-v1").exists());
    assert_eq!(fs::read(occupied.join("keep")).unwrap(), b"user value");
}

#[test]
fn dropped_prepared_owner_is_discovered_only_through_its_registration() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (batch, target) = batch(root.path(), "destination");
    let journal = batch.journal_path().to_owned();
    let registration = registry.register(batch.parent(), &journal).unwrap();
    let record = registration.path().to_owned();
    drop(registration);
    drop(batch);

    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].journal, journal);
    assert_eq!(reports[0].recovered, 1);
    assert_eq!(fs::read(&target).unwrap(), b"complete old value");
    assert!(!journal.exists());
    assert!(!record.exists());
    assert!(recovery::recover(&scratch).unwrap().is_empty());
}

#[test]
fn committed_owner_is_finalized_without_reverting_its_new_value() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (mut batch, target) = batch(root.path(), "destination");
    let registration = registry
        .register(batch.parent(), batch.journal_path())
        .unwrap();
    batch.publish().unwrap();
    drop(registration);
    drop(batch);

    assert_eq!(recovery::recover(&scratch).unwrap().len(), 1);
    assert_eq!(fs::read(target).unwrap(), b"complete new value");
}

#[test]
fn live_registration_is_skipped_without_destination_effects() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (mut batch, target) = batch(root.path(), "destination");
    let registration = registry
        .register(batch.parent(), batch.journal_path())
        .unwrap();

    assert!(recovery::recover(&scratch).unwrap().is_empty());
    assert_eq!(fs::read(target).unwrap(), b"complete old value");
    batch.abort().unwrap();
    registration.finish().unwrap();
    assert!(recovery::recover(&scratch).unwrap().is_empty());
}

#[test]
fn finalization_before_record_removal_is_idempotently_recovered() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (mut batch, target) = batch(root.path(), "destination");
    let registration = registry
        .register(batch.parent(), batch.journal_path())
        .unwrap();
    batch.publish().unwrap();
    batch.finalize().unwrap();
    drop(batch);
    drop(registration);

    let reports = recovery::recover(&scratch).unwrap();
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].recovered, 0);
    assert_eq!(fs::read(target).unwrap(), b"complete new value");
    assert!(recovery::recover(&scratch).unwrap().is_empty());
}

#[test]
fn renamed_parent_identity_is_not_rebound_to_an_unrelated_replacement() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (batch, target) = batch(root.path(), "destination");
    let parent = batch.parent().to_owned();
    let registration = registry.register(&parent, batch.journal_path()).unwrap();
    let record = registration.path().to_owned();
    drop(registration);
    drop(batch);
    fs::rename(&parent, root.path().join("moved-original-parent")).unwrap();
    fs::create_dir(&parent).unwrap();
    fs::write(&target, b"unrelated replacement").unwrap();

    let error = recovery::recover(&scratch).unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::InvalidData);
    assert!(error.to_string().contains("parent identity changed"));
    assert_eq!(fs::read(target).unwrap(), b"unrelated replacement");
    assert!(record.exists());
}

#[test]
fn corrupt_later_record_prevents_any_earlier_recovery_effects() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (batch, target) = batch(root.path(), "destination");
    let journal = batch.journal_path().to_owned();
    let registration = registry.register(batch.parent(), &journal).unwrap();
    drop(registration);
    drop(batch);
    let corrupt = registry.directory().join("record-ffffffff.json");
    fs::write(&corrupt, b"not JSON").unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&corrupt, fs::Permissions::from_mode(0o600)).unwrap();
    }

    assert!(recovery::recover(&scratch).is_err());
    assert!(
        journal.exists(),
        "all records must be checked before recovering any"
    );
    assert_eq!(fs::read(target).unwrap(), b"complete old value");
}

#[test]
fn recovery_does_not_scan_unregistered_destination_parents() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    let (registered, registered_target) = batch(root.path(), "registered");
    let registration = registry
        .register(registered.parent(), registered.journal_path())
        .unwrap();
    let (mut unregistered, unregistered_target) = batch(root.path(), "unregistered");
    let unregistered_journal = unregistered.journal_path().to_owned();
    unregistered.publish().unwrap();
    drop(registration);
    drop(registered);
    drop(unregistered);

    assert_eq!(recovery::recover(&scratch).unwrap().len(), 1);
    assert!(unregistered_journal.exists());
    assert_eq!(fs::read(registered_target).unwrap(), b"complete old value");
    assert_eq!(
        fs::read(unregistered_target).unwrap(),
        b"complete new value"
    );
}

#[test]
fn source_capture_excludes_only_the_registered_directory_identity() {
    let root = workspace();
    let source = root.path().join("source");
    let scratch = source.join(".temp");
    let user_registry_name = source.join("user/.msync-recovery-v1");
    fs::create_dir_all(&user_registry_name).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    fs::write(scratch.join("user-data"), b"ordinary scratch data").unwrap();
    fs::write(
        user_registry_name.join("keep"),
        b"same name is not ownership",
    )
    .unwrap();
    let registry = Registry::new(&scratch).unwrap();
    let mut store = SnapshotStore::new(&root.path().join("assets")).unwrap();
    store.exclude_registry(&registry).unwrap();

    let snapshot = store.capture(&source).unwrap();
    assert!(
        snapshot
            .entries()
            .contains_key(Path::new(".temp/user-data"))
    );
    assert!(
        snapshot
            .entries()
            .contains_key(Path::new("user/.msync-recovery-v1/keep"))
    );
    assert!(
        snapshot
            .entries()
            .keys()
            .all(|path| !path.starts_with(".temp/.msync-recovery-v1"))
    );
    assert!(store.capture(registry.directory()).is_err());
    assert!(store.capture(&registry.directory().join(".lock")).is_err());
}

#[test]
fn source_capture_refuses_a_replaced_registry_instead_of_hiding_the_new_directory() {
    let root = workspace();
    let source = root.path().join("source");
    let scratch = source.join(".temp");
    let registry = Registry::new(&scratch).unwrap();
    let mut store = SnapshotStore::new(&root.path().join("assets")).unwrap();
    store.exclude_registry(&registry).unwrap();
    fs::rename(registry.directory(), scratch.join("retired-registry")).unwrap();
    let replacement = Registry::new(&scratch).unwrap();
    fs::write(
        replacement.directory().join("foreign-value"),
        b"must not be hidden",
    )
    .unwrap();

    let error = store.capture(&source).unwrap_err();
    assert!(
        error
            .to_string()
            .contains("registry directory identity changed")
    );
    assert_eq!(
        fs::read(replacement.directory().join("foreign-value")).unwrap(),
        b"must not be hidden"
    );
}

#[cfg(unix)]
#[test]
fn symlink_registry_is_rejected_without_traversing_its_target() {
    use std::os::unix::fs::symlink;

    let root = workspace();
    let scratch = root.path().join("scratch");
    let unrelated = root.path().join("unrelated");
    fs::create_dir(&scratch).unwrap();
    fs::create_dir(&unrelated).unwrap();
    fs::write(unrelated.join("keep"), b"untouched").unwrap();
    symlink(&unrelated, scratch.join(".msync-recovery-v1")).unwrap();

    assert!(recovery::recover(&scratch).is_err());
    assert_eq!(fs::read(unrelated.join("keep")).unwrap(), b"untouched");
}
