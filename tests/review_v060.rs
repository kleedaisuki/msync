//! Independent integration regressions for ownership across structured regions.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("review-060-")
        .tempdir_in(root)
        .unwrap()
}

fn run(root: &Path, arguments: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(arguments)
        .output()
        .unwrap()
}

fn success(output: Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn constant_dead_arm_does_not_leave_relocations_to_discarded_nodes() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "if true:\n    \"kept\" -> selected\nelse:\n    $pwd::pwd/missing -> $pwd::pwd/discarded\n",
    )
    .unwrap();
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(root.path(), &["run", "program.mobj"]));
    assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"kept");
    assert!(!root.path().join("discarded").exists());
}

#[test]
fn dead_async_launch_in_macro_does_not_escape_as_a_phantom_join() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(root.path().join("entry.msync"), format!("macro task():\n    if false:\n        release child => $local::{executable}(\"--version\")\n    \"kept\" -> selected\ntask()\n")).unwrap();
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"kept");
}

#[test]
fn mutually_exclusive_branches_may_each_consume_the_same_program_port() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(root.path().join("switch"), b"on").unwrap();
    fs::write(root.path().join("entry.msync"), format!("release child => $local::{executable}(\"--version\")\nif exists switch:\n    child::stdout -> selected\nelse:\n    child::stdout -> other\nacquire child\n")).unwrap();
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read_to_string(root.path().join("selected"))
            .unwrap()
            .trim(),
        concat!("msync ", env!("CARGO_PKG_VERSION"))
    );
    assert!(!root.path().join("other").exists());
}

#[test]
fn a_program_launch_in_a_two_iteration_for_gets_fresh_runtime_identity() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    for name in ["a", "b"] {
        fs::write(root.path().join("inputs").join(name), name).unwrap();
    }
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(root.path().join("entry.msync"), format!("for item in inputs/<.+>:\n    child => $local::{executable}(\"--version\")\n    child::stdout -> version.txt\n")).unwrap();
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read_to_string(root.path().join("version.txt"))
            .unwrap()
            .trim(),
        concat!("msync ", env!("CARGO_PKG_VERSION"))
    );
}

#[test]
fn nested_range_query_does_not_overwrite_an_outer_function_parameter_slot() {
    let root = workspace();
    fs::create_dir_all(root.path().join("outer/a")).unwrap();
    fs::create_dir(root.path().join("inner")).unwrap();
    fs::write(root.path().join("outer/a/leaf"), b"outer child").unwrap();
    fs::write(root.path().join("inner/b"), b"inner file").unwrap();
    fs::write(root.path().join("entry.msync"), "boolean always(p):\n    return true\nboolean accepts(p):\n    return (inner/<.+> | ranges::any_of(always)) and exists p/leaf\nif outer/<.+> | ranges::any_of(accepts):\n    \"correct\" -> selected\nelse:\n    \"clobbered\" -> selected\n").unwrap();
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"correct");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn unrelated_exists_does_not_acquire_a_released_open_network_flow() {
    use std::process::Stdio;
    use std::time::Duration;
    use tokio::io::AsyncWriteExt;
    use tokio::net::TcpListener;

    struct Invocation(std::process::Child);
    impl Drop for Invocation {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }

    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    fs::write(root.path().join("gate"), b"present").unwrap();
    fs::write(root.path().join("entry.msync"), format!("wire => tcp://{}\nrelease wire::/ -> stream\nif exists \"gate\":\n    \"independent\" -> marker\n", listener.local_addr().unwrap())).unwrap();
    let mut invocation = Invocation(
        Command::new(env!("CARGO_BIN_EXE_msync"))
            .current_dir(root.path())
            .args(["run", "entry.msync"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap(),
    );
    let (mut peer, _) = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .expect("released flow was not admitted")
        .unwrap();
    peer.write_all(b"open stream").await.unwrap();
    let crossed_before_eof = tokio::time::timeout(Duration::from_secs(2), async {
        while !root.path().join("marker").exists() {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .is_ok();
    peer.shutdown().await.unwrap();
    drop(peer);
    let status = tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            if let Some(status) = invocation.0.try_wait().unwrap() {
                break status;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("CLI did not drain closed network flow");
    assert!(status.success());
    assert!(
        crossed_before_eof,
        "an unrelated existence check implicitly acquired a released open stream"
    );
    assert_eq!(
        fs::read(root.path().join("marker")).unwrap(),
        b"independent"
    );
}

#[test]
fn untrusted_flat_range_stage_chains_are_bounded_before_runtime_recursion() {
    use msync::compiler::{CompileContext, structured::Session};
    use msync::hir::{
        InstructionKind,
        control::{ControlInstruction, RangeStage},
    };

    let root = workspace();
    let mut module = msync::pipeline::compile_object_named(
        &mut Session::new(),
        "for file in inputs/<.+>:\n    file -> output\n",
        "entry.msync",
        &CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        },
    )
    .unwrap();
    let range = module
        .program
        .body
        .instructions
        .iter_mut()
        .find_map(|node| match &mut node.kind {
            InstructionKind::Control(ControlInstruction::For { range, .. }) => Some(range),
            _ => None,
        })
        .expect("range loop was not represented as typed control");
    // A flat JSON array does not exercise serde_json's nested-container limit.
    range.stages = vec![RangeStage::Drop(0); 1024];
    let error = module
        .validate()
        .expect_err("untrusted object allowed an unbounded recursive cursor chain");
    assert!(
        error.to_string().contains("range") || error.to_string().contains("stage"),
        "{error}"
    );
}

#[tokio::test]
async fn filename_strings_use_bound_logical_cwd_not_the_embedding_process_cwd() {
    use msync::compiler::{CompileContext, structured::Session};
    use msync::context::{Context, contextualize};
    use msync::execution::{Options, RuntimeSession};

    let root = workspace();
    let victim = "review-v060-only-in-logical-cwd";
    fs::write(root.path().join(victim), b"remove only the bound file").unwrap();
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let source = format!(
        "if exists \"{victim}\":\n    delete \"{victim}\"\n    \"correct\" -> selected\nelse:\n    \"ambient cwd\" -> selected\n"
    );
    let module = msync::pipeline::compile_object_named(
        &mut Session::new(),
        &source,
        "entry.msync",
        &context,
    )
    .unwrap();
    let bound = contextualize(&module, &Context::new(&context), &mut Session::new()).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.execute(&bound.program).await.unwrap();
    runtime.shutdown().await.unwrap();
    assert!(!root.path().join(victim).exists());
    assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"correct");
}
