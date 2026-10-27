# Runtime safety and transports

## Local execution

A compiled operation resolves local, `$env` and `$path` endpoints into native paths, enumerates terminal regex selectors (anchored full basename match), and preflights every selected tree before creating anything. A trailing slash on the destination means append the source basename and refuse existing destination files, including files nested inside a merging directory. Without the slash, an existing regular file may be overwritten and a new tree may be created at a missing target, but an existing directory is an error: append `/` to explicitly select container semantics. No implicit directory-name insertion is performed in overwrite mode.

The regular-copy executor rejects source/destination symbolic links, Windows reparse points, non-regular data entries routed to that executor, same-file copies (including hard links), and recursive copies into the source tree. Parent components are inspected too. `same-file` provides portable file identity; comparing path strings alone is insufficient. Each native file flow owns an exclusively created sibling stage. Publication uses atomic replace or native no-replace; the latter is never emulated with an existence check followed by overwrite. Preflight prevents predictable conflicts from causing partial tree mutation. Cancellation or failure discards the current native file stage and preserves its previous target (or keeps a new target absent). A directory expansion is not a whole-tree transaction: previously committed leaves and newly created directories remain. The planner/scheduler owns whole-script validation, resource dependencies, and concurrent execution. Hostile concurrent mutation of parent directories is outside this version's safety boundary: path-based portable Rust APIs cannot close all TOCTOU races.

Native finite file flows use the common platform I/O module: Windows `CopyFileExW` progress cancellation and Linux bounded `copy_file_range` calls, with an explicit bounded-buffer fallback when the platform lacks that capability. Both overwrite and no-clobber flows use the same unpublished stage and commit path. Source permissions are applied before publication. Directory traversal and preflight check the same cancellation token between entries.

## Transports

- HTTP(S) sources use the installed `curl` client (`--fail`, restricted protocols and redirects), download into project-local `.temp`, then pass through normal local copy checks. HTTP cannot enumerate selectors or be a destination. Leading `~/` is a logical endpoint-root prefix for HTTP, not a remote home directory.
- WSL uses `wsl --exec sh` and `wslpath` to map local paths. Remote filesystem operations run a guarded POSIX shell copy helper.
- SSH uses the installed `ssh` and `scp` clients and the user's established authentication/configuration. Arguments sent through a remote shell are single-quoted and embedded quotes escaped. SCP assumes modern OpenSSH's default SFTP transport; no legacy `scp -O` shell mode. Different endpoints stage through local `.temp`; uploads stage remotely through `mktemp -d` and clean the staging directory afterward. Authentication prompts are owned by the clients.
- Remote hosts require POSIX `sh` and GNU-compatible `find`, `realpath`, `cp`, `chmod`, `mktemp`, `stat`, `mv` and `ln`. Remote symlinks are rejected; FIFO/socket metadata is classified as a byte stream for dispatch to the separate stream executor rather than sent through recursive SCP. SSH transfers do not promise preservation of every filesystem metadata attribute, ACL or alternate stream.
- Dry-run is fully offline and performs no writes, network requests, or remote process launches. It describes compiled operations, not the existence/content of remote resources.

Local scratch directories are generated with exclusive directory creation and cleaned with RAII. They stay under the current project's `.temp`; remote staging necessarily lives on the target host. Payload bytes and authentication credentials are never telemetry. Resource paths, backend context, phases, committed/discarded byte counters and typed OS errors are intentionally available for diagnosis under the current user-authorized telemetry policy.

## Engineering references

- [Rust `fs::copy`](https://doc.rust-lang.org/std/fs/fn.copy.html): a same-file copy can truncate its input; the implementation selects platform-specific accelerated copy mechanisms.
- [Rust `OpenOptions::create_new`](https://doc.rust-lang.org/std/fs/struct.OpenOptions.html#method.create_new): atomically creates a new file and fails if a file or dangling symlink already exists.
- [GNU Coreutils `cp`](https://www.gnu.org/software/coreutils/manual/html_node/cp-invocation.html): established distinctions between recursive copies, existing directories and symbolic links motivate explicit, stricter DSL contracts rather than blindly forwarding `cp -r`.

Tests exercise actual temporary filesystem trees, collision preflight, same inode via hard links, overwrite versus preserve-name semantics, regex selection and dry-run side effects. These contracts are the basis for optimizing traversal without weakening safety.

## Verification on the implementation host

2026-10-25, Windows host with Ubuntu 24.04 under WSL:

- `cargo test --test runtime -- --test-threads=1`: 9 passed, 1 explicitly ignored WSL test. This includes 32 property-test cases for arbitrary binary contents and overwrite idempotence, and a real curl download from a Rust TCP HTTP fixture.
- `cargo test --test runtime -- --ignored --test-threads=1`: the real WSL round trip passed. It sends a file whose basename contains a single quote, semicolon and shell substitution syntax from Windows into WSL, checks the preserve-name collision failure, and copies it back. All fixture data stays in the project's `.temp` tree.
- The WSL journey exposed and fixed a production-relevant integration bug: `wsl -- command` invokes an extra shell layer that strips Windows path backslashes; `wsl --exec` preserves the intended native argument boundary.
- `cargo clippy --lib -- -D warnings` passed after runtime changes.

The initial implementation did not exercise authenticated SSH. The subsequent isolated WSL fixture below now covers the real Windows client/server path; it does not establish a complete SSH server/version matrix.

## Transport review hardening

A focused independent review found and fixed three concrete classes of boundary errors:

1. **Native staging names:** remote POSIX/HTTP basenames are validated as exactly one native `Component::Normal` before any transport starts. Windows additionally rejects backslash, slash and colon, so an absolute drive path or ADS cannot override `.temp` via `PathBuf::join`. Unrepresentable remote names fail safely instead of being silently normalized.
2. **Literal transport paths:** modern OpenSSH SCP's SFTP download implementation still calls `sftp_glob` with `GLOB_NOCHECK | GLOB_MARK`. Download source names escape `*`, `?`, `[`, `]` and backslash for this glob layer, while keeping `~/` available to home expansion. Upload destination paths are not glob-escaped. Reference: [OpenSSH `scp.c`, `sink_sftp`](https://github.com/openssh/openssh-portable/blob/master/scp.c). HTTP uses `curl --globoff`; an actual local HTTP test verifies one literal request for `/v1/a[1]%7Bx%7D%20file.html`, including URL-builder percent encoding of braces and spaces.
3. **Overlapping trees:** directory source and destination must be disjoint in both directions. Copying a directory into its ancestor can overwrite files inside the original source through a nested target mapping, so rejecting only descendants is insufficient. Both native and remote helpers reject ancestor overlap before mutation. Local uploads normalize `.` and `..` before deriving the preserved basename, and do not allocate local staging when the source is already local (avoiding accidental inclusion of a newly created staging tree in an upload).

Initial review verification: `cargo test --test review -- --nocapture` passed both independently authored regressions (staging escape and ancestor-overlap source mutation). `cargo test --test runtime -- --test-threads=1` passed 10 tests with the explicit WSL test ignored; the private boundary regressions and strict Clippy passed too. Authenticated SSH was added in the subsequent fixture below, without changing persistent SSH services, accounts, or configuration.

### Filesystem identity, not path spelling

Final containment review replaced lexical lowercase/prefix comparisons with file-identity ancestor checks. An ordinary Windows path and its `\\?\` verbatim alias (or 8.3/case alias) can name the same directory while having unrelated string prefixes. The runtime opens one `same_file::Handle` for the source root and compares it with each existing destination ancestor; if the target exists it checks that root against source ancestors too. Missing descendants therefore cannot evade containment through namespace aliases. A bounded Windows unit test invokes only `preflight`, verifies ordinary-source/verbatim-descendant and reverse-ancestor rejection, and asserts no directory was created. The remote POSIX helper uses the equivalent `test -ef` ancestor walk, covering WSL filesystem aliases as well.

Revalidated the three private transport/safety tests, the two independent review regressions, 10 runtime tests, the real WSL round trip, and warning-free library Clippy after this change.

## Real SSH/SFTP fixture (2026-10-25)

The explicit Windows integration target `tests/ssh.rs` starts the installed WSL
`sshd` using `tests/support/ssh_fixture.sh`. It runs the built **Windows msync
CLI** with the installed **Windows ssh/scp clients**, not WSL client substitutes
or mocked command wrappers. Tested client: OpenSSH_for_Windows 9.5p2; server:
Ubuntu 24.04 OpenSSH 9.6p1.

```powershell
cargo test --test ssh -- --ignored --nocapture --test-threads=1
```

All four explicit journeys passed, including the following real operations:

| Journey | Verified behavior |
| --- | --- |
| Upload/download/overwrite | 64 KiB binary roundtrip, embedded NUL, replacement of existing files, same-server copying, cross-endpoint staging, current-directory source |
| Recursive trees | Hidden files, nested binary files, empty directories, merge preserving unrelated target children, same-server directory copying |
| Conflict preflight | Local-to-SSH, SSH-to-local and SSH-to-SSH trailing-slash conflicts; existing bytes and unrelated entries remain unchanged; no partial predictable insertion |
| Literal names and selection | Brackets with conflicting decoy files, spaces, apostrophe, semicolon and literal command-substitution syntax; no shell injection; upload and download selectors match only direct complete basenames |
| Failures and cleanup | Missing source, unrepresentable nested Windows name, case-colliding remote entries, unauthorized identity, failed-upload staging cleanup, private structural telemetry |

### Isolation and lifetime

Each test exclusively allocates `.temp/ssh-fixture-*` with a fresh Windows
Ed25519 client identity, separate client/server configuration, a generated
server host key and a **prepopulated** private known-hosts file. Host-key checking
remains strict; password authentication and SSH-agent fallback are disabled.
`MSYNC_SSH_CONFIG` passes the private file to both native clients using `-F`.
When this variable is absent, normal client configuration behavior is unchanged;
the public `ExecutionOptions` shape and execution API are unchanged.

The daemon runs under `wsl --user root --exec unshare --mount --pid --fork
--kill-child=KILL --mount-proc`. Mount propagation is private. A 128 MiB tmpfs
mounted below the fixture directory supplies private `/run` and `/tmp` mounts.
The existing root account's authentication record is shadowed **only inside this
mount namespace**, enabling key-only login without editing the distribution's
account database. Login sessions are chrooted into a minimal `/run/jail` containing
the required shell, GNU helpers and their installed shared libraries; their home,
temporary data and transferred files live only in that tmpfs. No package,
account, system service, port-forwarding rule or persistent SSH setting is created.

The daemon listens only on 127.0.0.1 at a dynamically selected high port. Network
namespaces are intentionally shared so Windows can reach the real service. This
is a trusted-code test fixture, not a hostile multi-tenant sandbox or a claim that
privileged chroot sessions provide a complete security boundary.

Closing the fixture's stdin stops the supervisor; exiting PID-namespace init
terminates descendants, releases mounts and discards the in-memory filesystem.
An EOF/panic cleanup path and a 240-second supervisor deadline bound daemon
lifetime. Individual commands have deadlines and fixture-local output files.
Success and conflict cases assert no local/remote `msync.*` staging remains. After
shutdown, tests query the WSL listening sockets and verify authentication is no
longer possible. A Windows-side WSL forwarding proxy can briefly accept TCP after
the Linux daemon exits, so TCP connect alone is not a valid service-liveness test.

Each journey compares before/after fingerprints of Windows user/system SSH files
and WSL SSH files plus passwd/shadow/group. These assertions passed: existing
SSH configuration, known-hosts/authorized-key data, keys and account files were
unchanged. The fixture directory and generated client secrets are RAII-cleaned.

### Defects exposed by real clients

1. **Backslash escaping is not portable to Win32 SCP.** Its argument preprocessor
   converts backslashes to forward slashes before remote SFTP glob expansion.
   Consequently `a\\[1\\].txt` was sent as a different path. Literal glob
   characters now use single-character bracket classes (`[[]`, `[]]`, `[*]`,
   `[?]`), preserving tilde-home expansion and working without a backslash
   escape layer. Native Windows SCP explicitly rejects POSIX backslashes in
   remote paths instead of silently addressing a different file.
2. **Windows SCP also expands local upload arguments.** A local `a[1].txt` with
   an `a1.txt` decoy uploaded the decoy's contents. Existing Windows local source
   paths are now encoded with the same literal classes before this one expansion
   pass. Unix local argv remains untouched. The real decoy-file journey failed
   before the fix and now verifies byte-exact source selection in both directions.
3. **Checking only the root staging name is insufficient.** Recursive transfers
   can encounter native separators, device names, trailing-dot/space aliases and
   case collisions below a safe root. Remote validation now emits NUL-delimited
   relative-path/basename pairs; every basename and conservative Windows uppercase
   path key is checked before SCP begins. Unrepresentable trees fail explicitly
   without creating the DSL destination or changing an unrelated local victim.
   This deliberately rejects some names that a case-sensitive Windows directory
   might allow; it is not a claim to reproduce every NTFS Unicode collation rule.
4. **Directory-selector paths retain a trailing slash.** GNU `find` can preserve
   that slash in the starting entry's `%f`. The SSH preflight now resolves the
   already link-checked source with `realpath -e` before inventory, so valid
   selector roots are not incorrectly rejected as native filename separators.

These fixes retain the transport architecture and do not switch to legacy SCP,
disable host-key verification, introduce an SSH library, or modify the user's
SSH configuration. Primary references:
[Win32 SCP argument processing](https://github.com/PowerShell/openssh-portable/blob/latestw_all/scp.c),
[sshd flags and explicit configuration](https://man.openbsd.org/sshd.8),
[SSH configuration](https://man.openbsd.org/ssh_config),
[namespace lifecycle and mount propagation](https://man7.org/linux/man-pages/man1/unshare.1.html).
As with the original path-based runtime, hostile concurrent changes to remote
source trees between preflight and transfer are not a transactional guarantee.


## Plan-to-execution integration (2026-10-26)

The shared `crate::plan::FileClass` owns `Missing`, `Regular`, `Directory`, `Stream` and `Unknown`. Runtime hooks are intentionally small:

- `inspect_location(location, source)` reads only local metadata and resolves native PATH/ENV/home policy; remote filesystems remain `Unknown` offline. HTTP sources classify as regular download resources; TCP addresses classify as streams. Windows named-pipe namespaces are recognized before metadata access.
- `inspect_locations_live` additionally contacts WSL/SSH with bounded batches of fixed NUL-terminated metadata responses. Shell `test` operations classify regular files, directories, FIFO/socket streams and missing entries without opening any stream. Missing sources are returned as `Missing`, not immediately rejected: a prior operation may produce them.
- `local_path` is the shared native-path policy used by snapshot planning and the byte-stream executor; `remote_command_builder` exposes the shared correctly quoted SSH/WSL command construction without forcing buffered output collection.
- `execute_copy_flow` performs exactly one structural file/tree operation and returns committed counters/actions plus a typed cancellation outcome with a copy telemetry span. Stream dispatch and async lifetime/cancellation belong to the stream executor, not recursive copy.

The explicit destination rule is enforced in both native and remote helpers: a non-container destination that already exists as a directory is rejected regardless of source type. A terminal slash appends the source basename, recursively merges directories, and still fails on occupied regular-file leaves. Tests now assert rejection of both regular-file and directory sources targeting an existing unsuffixed directory, and successful trailing-slash merging. The authenticated isolated SSH journey has been updated accordingly instead of preserving the superseded in-place directory-merge behavior.

Concurrent disjoint copies may create the same missing parent. `ensure_directory` handles `AlreadyExists` by re-reading metadata and accepting only a genuine non-symlink directory; creation counters increment only for successful creators. An eight-thread property-style race test asserts all writers succeed and the aggregate count equals the actual two new directories.

### Integration verification after the destination-rule redesign

2026-10-26:

- `cargo test --test runtime --test review -- --test-threads=1`: runtime 11 passed / 1 explicit WSL ignored, independent review 2 passed.
- `cargo test --lib runtime::transport_tests -- --test-threads=1`: 6 passed / 1 explicit WSL metadata probe ignored. Coverage includes native file/missing/directory/TCP classification and nonexistent Windows named pipes (both slash spellings) without opening streams, safe namespace-alias overlap, literal staging/SFTP paths, remote recursive-name validation and eight-thread shared-parent creation.
- `cargo test --lib live_remote_metadata_probe -- --ignored --test-threads=1`: passed against installed WSL. Regular/Directory/Missing classifications leave source contents and fixture entry count unchanged.
- `cargo test --test ssh -- --ignored --test-threads=1`: all four authenticated, isolated OpenSSH/WSL journeys passed in 56 seconds, including the new unsuffixed-existing-directory rejection and successful explicit container merge. These fixtures also verify no lingering staging directories/listeners and byte-identical existing SSH configuration snapshots. This supersedes the earlier historical note that live SSH had not yet been verified.


### Public compatibility execution uses the unified pipeline

The existing `runtime::execute(program, options)` signature now delegates to `scheduler::prepare` and `scheduler::execute_plan` with default resource limits, rather than retaining an alternate sequential semantics. Dry-run preparation is offline and returns actions from the retained optimized nodes without running them. Manually constructed programs still reject read-only HTTP/PATH destinations before planning. Ordinary copy hooks remain independent, so the scheduler calls `execute_copy_cancellable` without recursing into compatibility execution.

The synchronous entry point explicitly rejects invocation inside an active Tokio runtime with a diagnostic recommending `scheduler::execute_plan_async`; it does not panic by trying to nest a runtime. Async embedders should prepare the plan outside latency-sensitive tasks (live remote metadata inspection is synchronous), then await the async execution API with explicit resource limits and cancellation.

Metadata classification is deliberately limited to the requested resource families: regular files, directories, FIFO/named pipes and sockets. Native block/character devices and other special entries are not silently treated as byte streams. The remote metadata helper follows the same rule.

Three actual-user-path regressions cover the compatibility wrapper: a later invalid directory target prevents an earlier file write, a regular binary file reaches a real TCP listener byte-for-byte through the scheduler, and calling the synchronous API in Tokio produces an actionable error rather than a panic. The runtime integration suite now passes 14 tests with one explicit WSL journey ignored by default; independent review remains 2/2 and library Clippy is warning-free.

The unified compatibility wrapper was also revalidated against the real WSL round trip (13 seconds) and all four authenticated isolated SSH journeys (52 seconds). The low-level copy executor no longer contains its former dry-run branch: dry-run is exclusively a plan-preparation/rendering operation, eliminating a second execution-policy path.

### Authenticated remote byte streams and shared HTTP transport

The optional Python SSH fixture extends the existing private mount/PID namespace rather than changing any distribution account, installed package or SSH configuration. Only its stream test copies the installed Python interpreter, standard library and linked libraries into a 256MiB project-local tmpfs chroot; the four ordinary SSH tests retain the smaller non-Python fixture. `mkfifo` and private `/proc` enable actual FIFO endpoints and post-cancellation process auditing. All authored Python fixture functions carry complete type annotations; copied third-party stdlib is unchanged.

`ssh_fifo_socket_and_cancellation_use_real_remote_helpers` passes real Windows OpenSSH to isolated WSL SSH server journeys for 256KiB of nontrivial binary data in both directions through FIFOs and Unix sockets. Socket readiness checks use metadata (`test -S`), not probe connections that would consume the only accepted stream. Each transfer closes at EOF and is compared byte-for-byte. A child test process receives its SSH config only through its own environment, cancels an idle FIFO with the public async scheduler token, and the namespace's `/proc` audit confirms no Python relay survives. Fixture shutdown retains the existing listener, staging and original-configuration fingerprint checks. The test passed in 22 seconds before the full SSH suite rerun.

Remote directory downloads now derive their staging basename from the validated root metadata listing, not raw spellings such as `~/` or `.`. The same fixture verifies both spellings copy the jail's small `/root` tree into a local container named `root` before any special entries are created.

HTTP download and HTTP-to-stream paths share `http_command_builder`: URL resolution, same-origin validation, HTTP(S)-only initial/redirect protocols, literal curl glob handling and no-buffer output are one policy. Its default stdout is suitable for bounded async forwarding, while ordinary downloads append `--output` using the same builder. No `--` delimiter traps later options; the validated URL always starts with HTTP(S), not an option prefix.

Final verification: all five authenticated SSH tests passed together in 82 seconds. The cancellation regression was then strengthened to observe an actually running remote Python FIFO relay through private `/proc` before triggering cancellation (rather than relying on a fixed delay); it passed again in 20 seconds and still verifies zero surviving helpers afterward. `cargo clippy --all-targets -- -D warnings` is clean after integration.


## Cancellable finite flows and atomic per-file publication (2026-10-27)

Finite files and IPC endpoints now share `io::FlowReport` / `io::FlowEnd` (`SourceClosed`, `DestinationClosed`, `Cancelled`). A directory operation structurally expands into child byte flows. The runtime's internal `execute_copy_flow` returns a typed `CopyOutcome`: committed `ExecutionReport`, discarded current-file bytes, an explicit cancellation flag and optional diagnostic context. The compatibility `execute_copy_cancellable` maps cancellation to the established `RuntimeError` API; public byte-flow consumers do not parse English messages to discover cancellation.

One `CopyContext` carries the caller's cancellation token and buffer budget through preflight, traversal, local kernel I/O and finite transport processes. OS errors retain `io::CopyFailure` progress before conversion into user diagnostics. `ExecutionReport.transferred_bytes` counts committed logical file bytes; cancelled current-file stage bytes are separate. A cancellation diagnostic identifies the unchanged current target, discarded uncommitted bytes and earlier committed work. Completion/publication is a linearization boundary: a cancellation observed after an already completed commit does not retroactively undo that file.

The common `io::StagedFile` is shared with asynchronous file sinks, not duplicated in runtime. Its exclusive sibling path is same-filesystem transaction storage, not an arbitrary project research temporary. `CopyFileExW` may delete that owned stage on `PROGRESS_CANCEL`; its RAII owner tolerates `NotFound` and clears Windows read-only attributes for cleanup. Linux uses bounded kernel-copy requests with token checks between requests. Native commit selects atomic replace versus no-replace (`MoveFileExW` / `renameat2(RENAME_NOREPLACE)` with a safe fallback); unsupported no-replace capabilities fail clearly rather than overwriting a concurrent creator. A single blocking kernel syscall may still wait on the storage device; cancellation is cooperative at OS progress/completion boundaries, not a promise to interrupt uninterruptible hardware I/O instantly.

Finite subprocess execution polls cancellation, kills/reaps the owned client, drains both pipes concurrently to avoid backpressure deadlocks, and bounds captured stdout at 8MiB and stderr at 64KiB. Remote per-leaf shell copies also use sibling `mktemp`, permission-preserving copy and atomic `mv`/no-clobber `ln`, with signal/exit cleanup traps. Remote tree expansion remains a sequence of committed leaves, not a server-wide rollback transaction. IPC helper lifetime is additionally validated by actual SSH future-abort and token-cancel tests.

### Verification

- A real Windows native-copy test reserves a 256MiB finite source, observes its destination stage become nonempty (the OS has opened the copy), then cancels through the public scheduler. Both pre-existing and absent targets retain the promised state, and no stage remains. It passed after wiring the scheduler's original token into the blocking worker; the test first exposed the previous fresh-token wrapper bug rather than merely checking pre-cancellation.
- The optional authenticated SSH fixture now tests both token cancellation and direct future abort while the token remains uncancelled. It observes the remote FIFO Python helper alive before interruption, then audits zero surviving helpers in the private PID namespace after each case. The same fixture retains byte-exact 256KiB FIFO/socket transfers in both directions and original SSH-config fingerprints.
- The normal runtime/review suite and private metadata/path/concurrency tests remain part of the regression workflow.

### Native implementation references

- [Microsoft `CopyFileExW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexw): `PROGRESS_CANCEL` deletes the partially copied destination, whereas `PROGRESS_STOP` retains it. The runtime therefore cancels only an exclusively owned unpublished stage.
- [Linux `copy_file_range`](https://man7.org/linux/man-pages/man2/copy_file_range.2.html): short kernel transfers and capability errors require a loop with resume-safe offsets, not an assumption that one syscall copies a whole file.
- [Linux rename/no-replace publication](https://man7.org/linux/man-pages/man2/rename.2.html): overwrite and atomic no-replace are distinct publication policies.

## Remote metadata batching: measured startup work, not a parser microbenchmark

`inspect_locations_live(backend, paths)` uses one shared metadata-only shell inspector and returns an ordered row for every input, including duplicates. A row is framed as `ok\0class\0` or `error\0message\0`; a symlink or unsupported device on one row does not erase later valid rows. The parser requires the exact record count and final NUL, and validates every class/tag. Missing is a valid class because an earlier graph node may produce it. No file data or stream endpoint is opened.

Each launch includes at most 8KiB of estimated escaped script/argument text, comfortably below Win32's command-line limit after quoting overhead. Oversized individual paths become their own item error, while other rows continue. The caller can group by backend and deduplicate resource queries without changing index/span mapping. Shell parent traversal uses string components rather than launching `dirname` for every ancestor. Telemetry records the path count, client-invocation count, per-chunk argument budget and per-item errors.

A reproducible ignored Windows/WSL test creates 16 distinct real files, warms the endpoint, compares 16 one-path invocations against one batch, and checks all classifications. On this implementation host the measured intervals were **1.774s serial versus 188.98ms batched (~9.4x)**. Setup/WSL startup is outside these timed intervals; this is actual native-client/remote-metadata work, not an in-memory timing surrogate. Another live test covers mixed Regular/Directory/Missing/error rows, duplicates and 160 rows spanning bounded chunks, with file contents and entry count unchanged.

### Preparation-owned native ancestor validation

`runtime::Inspection` belongs to one scheduler preparation. It shares successful ancestor-chain checks among sibling resources while still obtaining each distinct operand's own leaf metadata. The previous inspector queried that leaf twice and revalidated every shared ancestor for every operand. This mattered even for data mounted from native ext4 into project `.temp`: absolute pathname prefixes could still traverse DrvFs, so leaf storage identity alone did not make CLI metadata lookup cheap.

Only a completely validated chain enters the prefix set. A failed symlink/reparse/error chain cannot cache an inner prefix and cause a later sibling to bypass its failing outer ancestor. The cache does not canonicalize paths, collapse `..`, or survive the preparation. `inspection.leaf_probes`, `inspection.ancestor_probes` and `inspection.ancestor_hits` distinguish actual native metadata work from the existing classification/resolution-cache metrics. TCP, HTTP and Windows named-pipe classification still opens no stream data.

Execution and preflight continue using fresh `check_ancestors` calls, never the preparation's prefix set. A native Unix journey prepares a valid destination, replaces its parent with a directory symlink, then verifies that execution refuses it without modifying the redirected target; a new preparation independently refuses the link too. This is not a new guarantee against hostile concurrent replacement during a syscall.

Both fresh and cached checks guard ignored directory suffixes explicitly. `lstat("link/")` or `lstat("link/.")` can follow a directory symlink while `Path::ancestors` skips the final link spelling. A borrowed `components().as_path()` view trims those ignored suffixes before ancestor guards, retaining raw interior components and parent steps. It does not rebuild a Windows verbatim path with `push`, which could collapse parent steps. Native symlink tests cover `/`, `/.` and `/./` suffixes, failed-chain cache poisoning and raw `link/../safe`; Windows units retain verbatim parent-step and trailing-marker guards.


### Direct HTTP file sink and after-prefix reset verification

HTTP-to-local-file flows now write curl's decoded body directly into the final sink's owned transaction stage and publish only after the client exits successfully at EOF. This removes the former download-to-project-scratch followed by a second full kernel copy. It reuses the same `http_command_builder` as HTTP stream forwarding; URL origin/protocol/glob rules and native basename validation remain common. Destination ancestors and no-clobber policy are checked before the request. A failed/reset HTTP source discards its observed body prefix, preserves an old target or keeps a new target absent, and reports the discarded byte count and process error. Successful HTTP sinks count committed bytes like native files.

A real local HTTP fixture advertises a larger `Content-Length`, writes 64KiB, waits until that prefix is physically visible in the owned destination stage, then resets the TCP connection. Both existing and absent target cases pass: the operation fails, no partial file is published, and no stage remains. This is an actual after-open/after-prefix journey, not just a pre-cancelled token test.

The Windows after-prefix observers use metadata obtained from an open stage handle, not just directory enumeration metadata. The latter can be stale on NTFS while curl/native writers remain open; Microsoft explicitly recommends handle-based information when current attributes matter ([FindFirstFileW remarks](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfilew)). The HTTP reset journey passed four consecutive reruns after this observer correction, without loosening the requirement that the entire 64KiB prefix be accepted before reset.

Atomic publication here is a visibility guarantee, not an added fsync/crash-durability guarantee. Per-file fsync is intentionally not imposed on ordinary bulk-copy workloads; a whole-tree durable transaction is a separate policy.

Assignment publishes a new pathname binding. If another pathname hardlinks the previous destination inode, it retains its original bytes rather than receiving an in-place update. This is an intentional v0.3 semantic change: partial writes cannot be rolled back safely through arbitrary aliases, and queued zero-copy reads must keep the old source inode stable. Planner file identities still conservatively order alias conflicts; identity alone never authorizes eliminating a write.

### Remote regular-file interruption protocol

The authenticated SSH fixture now covers remote regular-file sinks, not just IPC helper lifetime. The helper's typed READY distinguishes regular files from streams; DATA frames carry bounded length-prefixed payload, and an explicit FINISH (rather than raw stdin EOF) is required before atomic file publication. COMMITTED acknowledges the regular-file commit. EOF caused by cancellation, source reset or future drop rolls back the uncommitted `.msync-flow-*.part`; FIFO/socket completion remains cancellable rather than inheriting a regular-file commit shield.

`ssh_remote_file_stages_rollback_after_prefix_reset_cancel_and_future_abort` passed against the isolated server: TCP resets, token cancellation and direct future abort each occur only after 4096 bytes are observed in the remote file stage, for both existing and absent targets. Helpers and stages are audited while the caller process and Tokio runtime remain alive, so process exit cannot hide a missing future-drop cleanup path. An empty finite source that has requested FINISH into a FIFO with no reader remains promptly cancellable. The producer fixture explicitly holds its connection until interruption; it does not confuse an unused TCP write half's EOF with the end of the producing stream.

### Structured v0.4 finite remote publication

The live engine routes finite file/literal writes to WSL/SSH through `execution::endpoint`, after physical pipe/socket classification. Local sources are read through the version manager; remote sources are imported at the actual logical transfer, not during planning. An immutable source is materialized only inside the invocation. Existing source/target aliases and directory overlap are rejected, including a local source's WSL pathname alias. HTTP/PATH remain read-only.

Transport populates a privately owned `.msync-v4.*` journal on the destination filesystem. The helper constructs a complete replacement root before touching the user binding. `/` container insertion recursively preflights collisions in that private clone; `/.` replaces the entire directory; an exact existing directory rejects. Missing parent trees are staged from their first absent ancestor. Source selectors use the same anchored Rust regex semantics over immutable direct-child basenames; an empty selection publishes nothing.

Publication retains the old root as a before-image, restores it on helper failure, and creates `COMMITTED` only after the replacement rename. An independent receipt query acquires the helper's `flock` before cleanup. This distinguishes a known committed assignment from cancellation or a lost SSH/WSL client acknowledgement; known COMMITTED wins a later client error. Unreachable or still-locked journals are retained and diagnosed as unknown commit state, never deleted speculatively. Finite remote publication requires the existing GNU-compatible tools plus `flock`. Rename publication provides runtime statement atomicity with compensation, not an OS-wide multi-path isolation or fsync/crash-durability promise against unrelated external writers.

Named remote transactions and program-output/remote publication cohorts still require a real versioned endpoint participant. The live preflight rejects them before any preceding statement executes; it never presents independent remote publication as a distributed transaction. No SSH configuration is changed. The explicit Windows `endpoint_execution_v4` integration target exercises native WSL filesystem destinations, Unicode/apostrophe paths, upload/download, directory replacement, container conflict rollback, literals, missing ancestors and named-transaction preflight. These are correctness journeys, not cross-filesystem performance measurements.

The finite remote implementation moves an already-owned payload into the private replacement root for exact assignments and absent container slots, eliminating a redundant full data copy before the publication rename. Existing container clones and merges request GNU `cp --reflink=auto`, preserving independent file identities with an ordinary-copy fallback on filesystems without copy-on-write cloning. The real WSL suite also covers a read-only before-image that makes cleanup fail after commit: the assignment succeeds with a retained-journal cleanup issue rather than being misreported as an unknown or failed transfer. A publication worker remains alive to cancel and settle its receipt if its caller drops the future; settlement timeout explicitly kills and joins the client instead of merely dropping its process future.
