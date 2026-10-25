# Runtime safety and transports

## Local execution

A compiled operation resolves local, `$env` and `$path` endpoints into native paths, enumerates terminal regex selectors (anchored full basename match), and preflights every selected tree before creating anything. A trailing slash on the destination means append the source basename and refuse existing destination files, including files nested inside a merging directory. Without the slash, files overwrite and directories merge at the specified path. No implicit directory-name insertion is performed in overwrite mode.

The runtime rejects source/destination symbolic links, Windows reparse points, unsupported special files, same-file copies (including hard links), and recursive copies into the source tree. Parent components are inspected too. `same-file` provides portable file identity; comparing path strings alone is insufficient. For no-clobber creation, `OpenOptions::create_new` enforces exclusivity atomically. Preflight prevents predictable conflicts from causing partial tree mutation. Operational failures such as disk-full can still leave partially copied trees; execution is sequential, not a filesystem transaction. Hostile concurrent mutation of parent directories is outside this version's safety boundary: path-based portable Rust APIs cannot close all TOCTOU races.

Overwrite copies use `std::fs::copy`, allowing native platform acceleration and permission copying. No-clobber copies use explicitly opened handles and `io::copy`, preserving the atomic create contract. There is no custom chunked-copy buffer or speculative thread pool.

## Transports

- HTTP(S) sources use the installed `curl` client (`--fail`, restricted protocols and redirects), download into project-local `.temp`, then pass through normal local copy checks. HTTP cannot enumerate selectors or be a destination. Leading `~/` is a logical endpoint-root prefix for HTTP, not a remote home directory.
- WSL uses `wsl -- sh` and `wslpath` to map local paths. Remote filesystem operations run a guarded POSIX shell copy helper.
- SSH uses the installed `ssh` and `scp` clients and the user's established authentication/configuration. Arguments sent through a remote shell are single-quoted and embedded quotes escaped. SCP assumes modern OpenSSH's default SFTP transport; no legacy `scp -O` shell mode. Different endpoints stage through local `.temp`; uploads stage remotely through `mktemp -d` and clean the staging directory afterward. Authentication prompts are owned by the clients.
- Remote hosts require POSIX `sh` and GNU-compatible `find`, `realpath`, `cp`, `chmod`, `mktemp`. Remote symlinks and special files are rejected. SSH transfers do not promise preservation of every filesystem metadata attribute, ACL or alternate stream.
- Dry-run is fully offline and performs no writes, network requests, or remote process launches. It describes compiled operations, not the existence/content of remote resources.

Local scratch directories are generated with exclusive directory creation and cleaned with RAII. They stay under the current project's `.temp`; remote staging necessarily lives on the target host. Secrets, paths and source text are not emitted in telemetry; execute and copy spans report structural status/duration only.

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
