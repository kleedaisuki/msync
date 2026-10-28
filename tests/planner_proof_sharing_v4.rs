//! Shared immutable graph proof footprints remain value-checked certificates.

use msync::compiler::{CompileContext, structured::Session};
use msync::{hir, preparation};
use std::fs;
use std::path::Path;
use std::sync::Arc;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("planner-proof-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, source: &str) -> hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

#[test]
fn independent_pair_proofs_share_immutable_node_effects_and_still_reject_tampering() {
    let root = workspace();
    for index in 0..3 {
        fs::write(root.path().join(format!("s{index}")), b"source").unwrap();
        fs::write(root.path().join(format!("d{index}")), b"before").unwrap();
    }
    let program = compile(root.path(), "s0 -> d0\ns1 -> d1\ns2 -> d2\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    let proofs = &prepared.graph.commutations;
    assert_eq!(proofs.len(), 3);
    let first = proofs
        .iter()
        .find(|proof| proof.left == hir::InstructionId(0) && proof.right == hir::InstructionId(1))
        .unwrap();
    let second = proofs
        .iter()
        .find(|proof| proof.left == hir::InstructionId(0) && proof.right == hir::InstructionId(2))
        .unwrap();
    assert!(Arc::ptr_eq(&first.left_effects, &second.left_effects));
    let mut tampered = prepared.clone();
    Arc::make_mut(&mut tampered.graph.commutations[0].left_effects).unknown_external = true;
    assert!(tampered.verify().is_err());
    prepared.verify().unwrap();
}
