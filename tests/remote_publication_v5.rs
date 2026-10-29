//! The structured interpreter must preserve finite remote publication semantics.

use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::{InstructionKind, Output};

#[cfg(windows)]
fn quoted(value: &str) -> String {
    serde_json::to_string(value).unwrap()
}

fn compile(work: &Path, source: &str) -> msync::hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: work.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

#[test]
fn future_remote_publication_is_compiled_without_opening_its_source() {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    let work = tempfile::tempdir_in(base).unwrap();
    let program = compile(
        work.path(),
        "Maker => $path::not-a-real-generator(\"future\")\n\
         $pwd / \"not-created\" -> $wsl::\"/tmp/msync-v5-unopened/target ' 可莉\"\n",
    );
    assert!(!work.path().join("not-created").exists());
    let blocks: Vec<_> = program.blocks().collect();
    let spawn_block = blocks
        .iter()
        .position(|block| {
            block
                .instructions
                .iter()
                .any(|instruction| matches!(instruction.kind, InstructionKind::Spawn { .. }))
        })
        .unwrap();
    let transfer_block = blocks
        .iter()
        .position(|block| {
            block
                .instructions
                .iter()
                .any(|instruction| matches!(instruction.kind, InstructionKind::Transfer { .. }))
        })
        .unwrap();
    assert!(spawn_block < transfer_block);
    let destination = program
        .body
        .instructions
        .iter()
        .find_map(|instruction| match &instruction.kind {
            InstructionKind::Transfer {
                destination: Output::File { location, .. },
                ..
            } => Some(location),
            _ => None,
        })
        .unwrap();
    assert_eq!(destination.path, "/tmp/msync-v5-unopened/target ' 可莉");
}

#[cfg(windows)]
mod wsl {
    use super::*;
    use std::process::{Command, Output as CommandOutput};
    use std::time::Duration;

    use msync::execution::{Options, RuntimeSession};

    const PREFIX: &str = "/tmp/msync-remote-v5.";

    fn probe(script: &str, arguments: &[&str]) -> CommandOutput {
        let output = Command::new("wsl.exe")
            .env("WSL_UTF8", "1")
            .args(["--exec", "sh", "-c", script, "sh"])
            .args(arguments)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "WSL probe failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        output
    }

    struct Fixture {
        local: tempfile::TempDir,
        remote: String,
    }

    impl Fixture {
        fn new() -> Self {
            let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
            fs::create_dir_all(&base).unwrap();
            let local = tempfile::Builder::new()
                .prefix("remote-v5-")
                .tempdir_in(base)
                .unwrap();
            let remote =
                String::from_utf8(probe("mktemp -d /tmp/msync-remote-v5.XXXXXXXXXX", &[]).stdout)
                    .unwrap()
                    .trim()
                    .to_owned();
            assert!(remote.starts_with(PREFIX));
            assert!(!remote[PREFIX.len()..].contains('/'));
            assert_eq!(
                probe("stat -f -c %t -- \"$1\"", &[&remote])
                    .stdout
                    .strip_suffix(b"\n")
                    .unwrap(),
                b"ef53",
                "the remote side must be WSL-native ext4, not /mnt/c or /mnt/d"
            );
            Self { local, remote }
        }

        fn endpoint(&self, suffix: &str) -> String {
            format!("$wsl::{}", quoted(&format!("{}/{suffix}", self.remote)))
        }

        async fn run(&self, source: &str) -> Result<(), msync::execution::ExecutionError> {
            let program = compile(self.local.path(), source);
            let mut runtime =
                RuntimeSession::new(&Options::new(self.local.path().join("scratch"))).unwrap();
            tokio::time::timeout(Duration::from_secs(60), runtime.execute(&program))
                .await
                .expect("finite remote publication did not finish within 60 seconds")?;
            Ok(())
        }

        fn assert_clean(&self) {
            let output = probe(
                "find \"$1\" -name '.msync-v4.*' -print -quit",
                &[&self.remote],
            );
            assert!(
                output.stdout.is_empty(),
                "a completed statement retained a remote journal: {}",
                String::from_utf8_lossy(&output.stdout)
            );
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            if !self.remote.starts_with(PREFIX) || self.remote[PREFIX.len()..].contains('/') {
                return;
            }
            let _ = Command::new("wsl.exe")
                .args([
                    "--exec",
                    "sh",
                    "-c",
                    "case $1 in /tmp/msync-remote-v5.*) chmod -R u+rwX -- \"$1\"; rm -rf -- \"$1\";; *) exit 1;; esac",
                    "sh",
                    &self.remote,
                ])
                .output();
        }
    }

    #[test]
    #[ignore = "subprocess fixture; invoked only by the generated-source journey"]
    fn remote_source_child() {
        let path = std::env::args()
            .find_map(|argument| argument.strip_prefix("remote-source:").map(str::to_owned))
            .expect("missing owned source path");
        fs::write(path, b"created by the previous basic block\n").unwrap();
    }

    #[tokio::test]
    #[ignore = "actual Windows to native-ext4 WSL finite publication; no SSH configuration changes"]
    async fn source_created_by_previous_program_is_uploaded_and_read_back() {
        let fixture = Fixture::new();
        let source = fixture.local.path().join("generated ' 可莉.txt");
        let executable = std::env::current_exe().unwrap();
        let arguments = [
            "--exact".to_owned(),
            "wsl::remote_source_child".to_owned(),
            "--ignored".to_owned(),
            "--nocapture".to_owned(),
            "--skip".to_owned(),
            format!("remote-source:{}", source.display()),
        ];
        let script = format!(
            "Maker => $local::{}({})\n\
             $pwd / \"generated ' 可莉.txt\" -> {}\n\
             {} -> $pwd / \"roundtrip.txt\"\n",
            quoted(&executable.to_string_lossy()),
            arguments
                .iter()
                .map(|argument| quoted(argument))
                .collect::<Vec<_>>()
                .join(", "),
            fixture.endpoint("nested/target ' 可莉.txt"),
            fixture.endpoint("nested/target ' 可莉.txt"),
        );
        assert!(!source.exists());
        fixture.run(&script).await.unwrap();
        assert_eq!(
            fs::read(fixture.local.path().join("roundtrip.txt")).unwrap(),
            b"created by the previous basic block\n"
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual Windows to native-ext4 WSL tree/container conflict journey"]
    async fn recursive_replacement_and_container_conflict_preserve_whole_statement() {
        let fixture = Fixture::new();
        let tree = fixture.local.path().join("tree ' 可莉");
        fs::create_dir_all(tree.join("nested/empty")).unwrap();
        fs::write(tree.join("nested/data"), b"first").unwrap();
        fs::write(tree.join("obsolete"), b"removed by replacement").unwrap();
        fixture
            .run(&format!(
                "$pwd / \"tree ' 可莉\" -> {}\n",
                fixture.endpoint("target")
            ))
            .await
            .unwrap();
        fs::remove_file(tree.join("obsolete")).unwrap();
        fs::write(tree.join("nested/data"), b"second").unwrap();
        fixture
            .run(&format!(
                "$pwd / \"tree ' 可莉\" -> {} /.\n",
                fixture.endpoint("target")
            ))
            .await
            .unwrap();
        probe(
            "test ! -e \"$1/target/obsolete\"; test -d \"$1/target/nested/empty\"; test \"$(cat \"$1/target/nested/data\")\" = second",
            &[&fixture.remote],
        );
        let error = fixture
            .run(&format!(
                "$pwd / \"tree ' 可莉\" -> {}\n",
                fixture.endpoint("target")
            ))
            .await;
        assert!(
            error.is_err(),
            "an existing exact directory is not a container"
        );

        fixture
            .run(&format!(
                "$pwd / \"tree ' 可莉\" -> {} /\n",
                fixture.endpoint("container")
            ))
            .await
            .unwrap();
        fs::write(tree.join("aaa-new"), b"must not leak before collision").unwrap();
        assert!(
            fixture
                .run(&format!(
                    "$pwd / \"tree ' 可莉\" -> {} /\n",
                    fixture.endpoint("container")
                ))
                .await
                .is_err()
        );
        probe(
            "test ! -e \"$1/container/tree ' 可莉/aaa-new\"; test \"$(cat \"$1/container/tree ' 可莉/nested/data\")\" = second",
            &[&fixture.remote],
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual Windows to native-ext4 WSL literal overwrite journey"]
    async fn literal_overwrite_is_visible_to_the_following_read() {
        let fixture = Fixture::new();
        fixture
            .run(&format!(
                "\"first\" -> {}\n\"last\" -> {}\n{} -> $pwd / \"download\"\n",
                fixture.endpoint("data"),
                fixture.endpoint("data"),
                fixture.endpoint("data"),
            ))
            .await
            .unwrap();
        assert_eq!(
            fs::read(fixture.local.path().join("download")).unwrap(),
            b"last"
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual Windows to native-ext4 WSL permission and directory-mode guards"]
    async fn readonly_target_preserves_bytes_mode_and_leaves_no_publication_journal() {
        let fixture = Fixture::new();
        probe(
            "printf kept > \"$1/data\"; chmod 0444 -- \"$1/data\"; test -w \"$1\"",
            &[&fixture.remote],
        );
        let error = fixture
            .run(&format!("\"new\" -> {}\n", fixture.endpoint("data")))
            .await
            .unwrap_err();
        assert!(error.to_string().contains("read-only"), "{error}");
        probe(
            "test \"$(cat \"$1/data\")\" = kept; test \"$(stat -c %a -- \"$1/data\")\" = 444",
            &[&fixture.remote],
        );
        fixture.assert_clean();

        probe("chmod 0640 -- \"$1/data\"", &[&fixture.remote]);
        fixture
            .run(&format!("\"literal\" -> {}\n", fixture.endpoint("data")))
            .await
            .unwrap();
        probe(
            "test \"$(cat \"$1/data\")\" = literal; test \"$(stat -c %a -- \"$1/data\")\" = 640",
            &[&fixture.remote],
        );

        fs::create_dir(fixture.local.path().join("directory")).unwrap();
        assert!(
            fixture
                .run(&format!(
                    "$pwd / \"directory\" -> {}\n",
                    fixture.endpoint("data")
                ))
                .await
                .is_err()
        );
        probe(
            "test -f \"$1/data\"; test \"$(cat \"$1/data\")\" = literal",
            &[&fixture.remote],
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual native-ext4 WSL selectors must acquire only matched direct children"]
    async fn selector_download_excludes_unselected_special_and_unreadable_payloads() {
        let fixture = Fixture::new();
        probe(
            r#"set -eu
mkdir -p "$1/source/picked/empty" "$1/source/private"
printf chosen > "$1/source/keep.txt"
printf tree > "$1/source/picked/nested"
printf secret > "$1/source/private/data"
chmod 000 "$1/source/private"
mkfifo "$1/source/unselected-fifo"
ln -s /missing "$1/source/unselected-link"
n=$(printf '\377'); printf invalid-name > "$1/source/$n"
"#,
            &[&fixture.remote],
        );
        let source = format!(
            "source := {}\nsource/<keep\\.txt|picked> -> selected/\nsource/<absent> -> no-selection/\n",
            fixture.endpoint("source")
        );
        let program = compile(fixture.local.path(), &source);
        let mut runtime =
            RuntimeSession::new(&Options::new(fixture.local.path().join("scratch"))).unwrap();
        let report = tokio::time::timeout(Duration::from_secs(60), runtime.execute(&program))
            .await
            .expect("selected WSL acquisition blocked on an unselected endpoint")
            .unwrap();
        runtime.shutdown().await.unwrap();
        assert_eq!(report.assigned_files, 2);
        assert_eq!(report.assigned_bytes, 10);
        assert_eq!(
            fs::read(fixture.local.path().join("selected/keep.txt")).unwrap(),
            b"chosen"
        );
        assert_eq!(
            fs::read(fixture.local.path().join("selected/picked/nested")).unwrap(),
            b"tree"
        );
        assert!(fixture.local.path().join("selected/picked/empty").is_dir());
        assert_eq!(
            fs::read_dir(fixture.local.path().join("selected"))
                .unwrap()
                .count(),
            2
        );
        assert!(!fixture.local.path().join("no-selection").exists());
        let error = fixture
            .run(&format!(
                "source := {}\nsource/<unselected-fifo> -> rejected/\n",
                fixture.endpoint("source")
            ))
            .await
            .unwrap_err();
        assert!(error.to_string().contains("special"), "{error}");
        assert!(!fixture.local.path().join("rejected").exists());
        probe(
            "test \"$(stat -c %a -- \"$1/source/private\")\" = 0; chmod 0700 -- \"$1/source/private\"",
            &[&fixture.remote],
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual WSL-to-WSL generic import must retain contextual root provenance and metadata"]
    async fn generic_wsl_contextual_root_keeps_actual_name_data_and_portable_metadata() {
        let fixture = Fixture::new();
        probe(
            r#"set -eu
mkdir -p "$1/source/nested/empty" "$1/copied"
printf 'chosen\000bytes' > "$1/source/nested/data"
printf retained > "$1/copied/keep"
touch -d '@978307200' "$1/source/nested/data"
touch -d '@978307202' "$1/source/nested/empty"
touch -d '@978307204' "$1/source/nested"
touch -d '@978307206' "$1/source"
"#,
            &[&fixture.remote],
        );
        let source = format!(
            "{} -> {} /\n",
            fixture.endpoint("source/."),
            fixture.endpoint("copied")
        );
        let program = compile(fixture.local.path(), &source);
        let mut runtime =
            RuntimeSession::new(&Options::new(fixture.local.path().join("scratch"))).unwrap();
        let report = tokio::time::timeout(Duration::from_secs(60), runtime.execute(&program))
            .await
            .expect("generic finite WSL import did not finish")
            .unwrap();
        runtime.shutdown().await.unwrap();
        assert_eq!(report.assigned_files, 1);
        assert_eq!(report.assigned_bytes, 12);
        assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
        probe(
            r#"set -eu
[ -d "$1/copied/source/nested/empty" ]
[ "$(cat "$1/copied/keep")" = retained ]
cmp "$1/source/nested/data" "$1/copied/source/nested/data"
for p in source source/nested source/nested/empty source/nested/data; do
    a=$(stat -c %Y -- "$1/$p")
    b=$(stat -c %Y -- "$1/copied/$p")
    [ "$a" = "$b" ] || { echo "modification time mismatch $p: $a != $b" >&2; exit 1; }
done
[ "$(find "$1/copied" -mindepth 1 -maxdepth 1 | wc -l)" = 2 ]
"#,
            &[&fixture.remote],
        );
        fixture.assert_clean();
    }

    #[tokio::test]
    #[ignore = "actual native-ext4 WSL directory locking must not modify or create sidecar files"]
    async fn parent_directory_lock_preserves_user_sidecar_and_creates_no_reserved_file() {
        let fixture = Fixture::new();
        probe(
            "printf 'user\\000lock\\377' > \"$1/.msync-publication.lock\"; chmod 0640 -- \"$1/.msync-publication.lock\"; touch -d '@978307200' -- \"$1/.msync-publication.lock\"",
            &[&fixture.remote],
        );
        let inspect = "stat -c '%a:%Y:%s:%i' -- \"$1/.msync-publication.lock\"; sha256sum -- \"$1/.msync-publication.lock\"";
        let before = probe(inspect, &[&fixture.remote]).stdout;
        fixture
            .run(&format!("\"first\" -> {}\n", fixture.endpoint("data")))
            .await
            .unwrap();
        assert_eq!(probe(inspect, &[&fixture.remote]).stdout, before);
        fixture.assert_clean();

        probe(
            "mkdir -- \"$1/clean\"; test ! -e \"$1/clean/.msync-publication.lock\"",
            &[&fixture.remote],
        );
        fixture
            .run(&format!(
                "\"second\" -> {}\n",
                fixture.endpoint("clean/data")
            ))
            .await
            .unwrap();
        probe(
            "test ! -e \"$1/clean/.msync-publication.lock\"; test \"$(cat \"$1/clean/data\")\" = second",
            &[&fixture.remote],
        );
        assert_eq!(probe(inspect, &[&fixture.remote]).stdout, before);
        fixture.assert_clean();
    }
}
