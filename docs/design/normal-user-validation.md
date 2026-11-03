# Retryable normal-user transfer acceptance

## Expected user journey

The 2026-11-01 user decision replaces automatic recovery/rollback with retryable
file transfers. Ordinary files use same-parent staging plus replacement and at
most one file fsync. A finite remote transfer owns one native SSH exec channel
for discovery, payload and completion. Old registries are neither prerequisites
nor cleanup authority. Directory/cohort failure may leave completed members;
the next transfer is an ordinary retry rather than a recovery ritual.

`tests/normal_user_model.rs` checks the public executable, not internal protocol
functions:

- ordinary file overwrite and literal overwrite preserve requested bytes;
- source/object/IR execution ignores existing malformed or group-readable local
  and remote registries, leaving their contents untouched;
- missing-source failure preserves the existing ordinary target and retry works
  after the source is created;
- selected linked workspaces work, native executable modes survive, and relative
  and dangling symlink entries survive a directory transfer on Unix.

`tests/normal_ssh_model.py` uses the existing private real OpenSSH fixture, with
ControlMaster disabled and native client PATH wrappers. It checks one ssh,
zero scp, one accepted authentication and one server exec session for each finite
upload, overwrite, download, tree, cohort, literal-name and retry journey. The
fixture independently reads loopback destination bytes, modes and nanosecond
mtimes. Newline, Unix backslash and Unicode pathnames are ordinary transfer data,
not reasons to reject a valid native path. The same harness traces six-byte
local new/overwrite copies and requires no syncfs and at most one fsync/fdatasync.
The trace count is a syscall count, not a physical-device-flush claim.

Commands once the integrated source is stable:

```sh
cargo test --locked --test normal_user_model
python3 tests/normal_ssh_model.py /absolute/path/to/msync \
  --output .temp/normal-user-model/results.json
```

These are native Linux client/server journeys. They establish actual OpenSSH
protocol behavior and client counts, not native Windows UI or WAN latency.
The harness records binary and probe hashes so a concurrently replaced image is
not silently accepted.

## Accepted focused journey

Frozen integrated debug image SHA-256
`20f53498c6ace3828e21941430272f0f660d829e158f6e88846990b1c4598e6d`
passed all **12 actual OpenSSH journeys**. Every journey used exactly one native
ssh process, zero scp processes, one accepted authentication and one server exec
session, including the expected failing NotADirectory case and its successful
ordinary retry:

1. 1,795-byte upload with exact nanosecond modification time and native mode;
2. ordinary overwrite;
3. download;
4. literal overwrite retaining target mode;
5. same-endpoint remote copy;
6. recursive directory with relative/dangling symlinks and executable mode;
7. selected finite cohort;
8. repeating that cohort with updated file bytes;
9. repeating a directory merge while retaining unrelated sibling data;
10. newline/backslash/space/Unicode pathname;
11. incompatible ordinary parent error, preserving that parent's bytes;
12. retry after correcting the actual parent condition.

The same image's Linux syscall journey observed **one fsync and zero syncfs**
for both new and existing ordinary six-byte targets. A rename installed each
target from its own parent directory; no explicit target unlink preceded the
replacement. No persistent protocol directory was created. The retained result
is `.temp/normal-user-model/ssh-candidate.json`, fixture
`.temp/normal-user-model/s-qt5ywh0k`; the maintained harness contains all expected
bytes/metadata and count assertions, so cleanup does not erase the procedure.

`cargo test --locked --test normal_user_model -- --nocapture` passed four
actual-CLI tests. `cargo test --locked --test recovery_concurrency_v5 -- --nocapture`
passed the independent runtime/live TCP stream journey. The tests cover normal
Linux user behavior; native Windows authentication UI and runtime are not
inferred from these results.

## Defects exposed during integration

The initial integrated debug image SHA-256
`cea3a9264d2c6da609c0b87dbe80ae3ec26708013f9dd0abc0cb045207ab6177`
passed all four `normal_user_model` CLI journeys and the rewritten independent
runtime/live-stream journey. The actual Linux syscall probe recorded exactly
**one fsync** and **zero syncfs** for each six-byte new-file and overwrite case,
with a rename installing the target and no persistent protocol directories.
This is a count comparison with the earlier 39-fsync lifecycle, not a timing
claim. The retained report is `.temp/normal-user-model/local-syscalls.json`.

The first frozen-image SSH probe accepted a 1,795-byte upload and overwrite with
one client/authentication/session each, but exposed a remaining download
pre-inspection: download succeeded while using two authenticated clients.
`ordinary::remote_kind` still called `inspect_locations_live` before the archive
download. The storage owner removed that admission path; final acceptance above
confirms one client rather than accepting the extra round trip.

The next frozen image
`35667d4aae2178c896c9b994b7dd66f0a3c633096ba56a06689e17896e4d3222`
passed nine SSH journeys with exactly one client/authentication/session each:
upload, overwrite, download, literal overwrite, same-endpoint copy, symlink tree,
cohort insert, repeated cohort overwrite and repeated tree merge preserving an
unrelated sibling. It then exposed a remaining parser/compiler policy: a valid
Unix filename containing newline/backslash/space was rejected as boundary
whitespace before transport. The guard split backslashes as separators on every
platform and independently rejected quoted component whitespace. The language
owner removed that refusal; final acceptance preserves the original native-path
fixture rather than weakening it.

## Superseded tests and retained behavior

The following exclusively obsolete targets were removed with the implementation,
rather than used as reasons to preserve the protocol under another name:

| Test target | Superseded expectations | Replacement/retained value |
| --- | --- | --- |
| `empty_recovery_v6.rs` | Empty source/object/IR must recover abandoned fallback state; malformed registry blocks declarations; repeated empty admission scans late abandoned state. | Normal source/object/IR ignores and preserves old state; no scratch is needed for declarations. |
| `recovery_registry_v5.rs` | Registry claiming, live record locks, coordinator-before-stage order, startup collection, owner-marker authority, invalid-registry rejection. | Live temporary owner cleanup and not deleting unrelated paths remain useful, without persistent record authority. |
| `storage_crash_v5.rs` | SIGKILL cuts restore an entire statement; commit decision preserves complete new state; startup repairs temporary directory mode. | One ordinary replacement is not partially visible; failed multi-file work can be retried. No entire-statement rollback promise. |
| `storage_stage_crash_v5.rs` | Process death causes later startup to discover/remove exactly its registered stage. | Live owner cleans its own resources; old temporary data is not scanned as execution prerequisite. |
| `barrier_crash_v5.rs` | Every empty/declaration invocation performs global recovery; tree interruption restores complete old tree. | Existing completed members survive, no unrelated earlier statement is reverted, retry requires no recovery command. |
| `recovery_concurrency_v5.rs` (rewritten, not removed) | A concurrent visitor inspects/retains locked live records and collects a dead registration. | Keep the actual user story: a second session copies while an earlier session owns an unfinished stream; no startup registry interaction. |

`filesystem_v5.rs` mixes useful snapshot/metadata cases with rollback protocols.
Retire or replace these named cases:

- `publication_receipts_restore_previous_snapshots_without_retained_history`;
- `publication_failures_restore_before_images_and_retain_retryable_journals`;
- `failed_compensation_retains_both_versions_and_requires_explicit_recovery`;
- `cleanup_failure_is_visible_retains_the_journal_and_can_be_retried`;
- `another_owners_replacement_is_never_overwritten_during_rollback`.

Split the successful tree/metadata assertions from the rollback calls in
`tree_manifest_retains_empty_directories_and_exact_publication_replaces_the_root`,
`absence_is_a_tombstone_not_an_empty_file_in_both_directions`,
`a_native_install_failure_restores_the_original_target_before_returning`, and
`file_and_directory_modes_survive_snapshot_publication_and_rollback`.
Snapshot immutability for actual versioned/fan-out consumers, literal pathname
representation, file modes and unaffected hard-link metadata remain behavior
checks, not security policy. Replace
`symlink_entries_and_ancestors_are_rejected_without_following_them` with actual
link-preservation and selected-alias acceptance.

`tests/support/statement_storage.rs` and `execution_workspace_v4.rs` were updated
to recognize an exact empty live ephemeral root, or no scratch at all for an
ordinary transfer. They no longer demand a persistent stage record or
`.msync-owned-stage-v1` marker merely to satisfy an assertion helper. Likewise,
`selected_capture_v5.rs::containing_source_retains_user_temp_data_but_excludes_exact_private_owners`
should check the live invocation exclusion, not a newly created recovery index.

Container acceptance now includes overwriting a repeated same-name ordinary file
and repeating a tree merge while retaining unrelated siblings. This is normal
idempotent copying, not a collision-defense or whole-container rollback test.

## Maintained cohort/workspace runner

`tests/ssh_cohort_v065.py` retains its public `--whole-cwd-only` and
`--repl-capture-only` selections, but no longer requires the retired journal,
rollback, implicit master or pre-transfer remote-alias refusal. Cohort repetition
overwrites ordinary files; a source already inside the selected destination
remains a valid source. Its single-exchange trace check requires one
`ssh.transfer` span with `native_single_exec`, one recorded client invocation and
one actual authenticated exec session in short and long workspace paths. It does
not manufacture ALLOCATE/PUBLISH/SETTLE spans that no longer exist.

Whole-workspace and later local-only REPL journeys retain user `.temp` data and
assert no implicit `-M`, `-O` or control-socket `-S` clients. A user-owned socket
must remain untouched and yield the actual unsupported-node error; a failed
directory copy is no longer required to roll back all prior progress.

Focused manual selections avoid repeating the unrelated cases:

```sh
python3 tests/ssh_cohort_v065.py BINARY --trace-only --output .temp/trace-copy.json
python3 tests/ssh_cohort_v065.py BINARY --whole-cwd-only --output .temp/workspace-copy.json
python3 tests/ssh_cohort_v065.py BINARY --cohorts-only --output .temp/cohort-copy.json
```

The cross-source remote cohort case checks copied bytes and valid alias behavior;
it must not be described as a one-channel same-endpoint batch unless client
grouping is implemented and independently counted. The dedicated normal-model
runner provides the explicit per-transfer client-count acceptance above.

The maintained runner's changed scopes pass against the frozen release image
`111af19ba4e380fd764892dc2b0b8f78b461edb696a67e5855703bad8e51398b`:
`--trace-only` passes two short/long workspace checks;
`--whole-cwd-only` passes local/remote whole-workspace copies, the untouched
user-socket failure and later local-only REPL copy; `--cohorts-only` passes six
normal cohort/merge/alias/empty/literal checks. These are 12 focused checks of
the changed maintained-runner expectations, not another blanket regression.
The immutable reports are `.temp/normal-user-model/maintained-{trace,workspace,cohorts}.json`
while retained; they identify tested harness hash
`57812f3f21fccfd94b1e3cd67bce21b9ba459f286d64fad60aa871f41a8e327d`.

`remote_publication_v5.rs` has ordinary source/selector/metadata/collision
journeys worth preserving. Its Windows-only explicit directory-lock journey
must cease requiring a parent-wide lock; retain its useful observation that
unrelated user sidecar files are not modified. No full regression matrix is
needed to discover these direct contract changes.

## Final integrated release image

The final unreleased Linux image SHA-256 is
`1d03960511bf88368e24f5c884933d91c631ec07a9c5e700f45c8568227a0330`.
It is a working-tree build, not a replacement of published v0.6.5 assets. The
ordinary-copy/retry decision was explicitly reconfirmed after a later instruction
snapshot repeated the historical recovery paragraphs; those old paragraphs are
void, as recorded in `AGENTS.md`.

The exact final image passes the maintained primary runner's 12 actual SSH
journeys. Every case uses **one SSH client, zero SCP, one accepted authentication
and one remote exec session**, including the named failing-parent case and its
successful retry. Bytes, 1,795-byte payloads, nanosecond modification times,
permissions, link entries, literal names and repeated container merges are
verified independently. The local new/overwrite syscall cases each use exactly
one fsync and zero syncfs, a same-parent stage/rename and no target unlink before
replacement. Report: `.temp/normal-user-model/final-release.json` while retained.

The same image passes `tests/ssh_channel_closure.py`: a real 2,200-file bare-regex
selection with 279,404 bytes of selected names and a 512 KiB first payload uses
one SSH client; both blocked FIFO cancellation directions release all five owned
remote descendants and leave no delayed reader/writer; same-server source arrays
use one exec without local payload roundtrips; read-only duplicate directory
aggregation and staged-link replacement preserve outside data; native `.`/`..`
markers resolve their actual directory while `file/.` reports NotADirectory.
Report: `.temp/normal-user-model/final-closure.json` while retained.

Two final traced uploads in short and long workspaces pass on that image. The
single-exec trace carries endpoint, PID, process setup/wait/drain timings and
ready/archive/completion events without old allocation/publication/settlement
spans or implicit master clients. Report:
`.temp/normal-user-model/final-trace.json` while retained.

A separate actual CLI regression verifies `first -> target; target -> target`
with first `A` and old target `old`: target becomes `A`, not the stale old value.
The fold implementation now carries acquired bytes/metadata instead of performing
three later path/identity/mode revalidations. Initial semantic admission and the
real same-program target-read dependency remain; the old Guard/Stamp/Traversal
machinery and fold-only snapshot/materialization are removed.

Final local strict Clippy passes for lib/bin on Linux and the Windows GNU target.
All Cargo targets compile, formatting and diff checks pass. Windows cross checks
use the project-managed Clang/mingw headers setup already described in the SSH
performance note; they are not native Windows runtime or UI execution. PowerShell
selector tests were not run because `pwsh` is unavailable; static selector updates
remove the retired target names without adding a quality gate.

Final fold-only acceptance passes 11 cases, including 32 generated stable file
chains and explicit directory/link controls. These preserve real same-program
name dependencies while removing all repeated post-acquisition public-path,
permission and identity checks. There are no pending storage test runs or source
changes behind the final image results above.

## v0.6.6 release preparation

The version-bumped local release executable (`msync 0.6.6`), SHA-256
`0a2c15e4ae90a20129b0418c49ef035fd1b973b7302702108624e63772f5a708`, passed the same
12 real OpenSSH journeys and local syscall checks, plus the large-selector,
FIFO cancellation, same-host cohort and native-directory-marker closure suite.
Retained reports: `.temp/release-v066/ssh.json` and
`.temp/release-v066/closure.json`. This is a local Linux image, not the later
Actions-packaged Linux or Windows binary. Release packaging independently builds
and exercises the distributable assets.

## Published v0.6.6 assets and validation status

Released on 2026-11-01 at 15:48:14 Asia/Singapore from commit
`5b288e914887e04928626cc5067d0a1a08d95ae8`:
[release](https://github.com/kleedaisuki/msync/releases/tag/v0.6.6),
[successful package workflow](https://github.com/kleedaisuki/msync/actions/runs/41285893473).
The immutable tag resolves to that exact source commit. Windows static-CRT
packaging and native install/upgrade/repair/PATH/transfer/uninstall, Linux portable
object/IR replay, and the actual release-binary VS Code host passed.

All four public assets were downloaded and matched the downloaded SHA256SUMS.
The downloaded Linux binary reports `msync 0.6.6`; the VSIX manifest agrees.
Windows ZIP contains the PE executable and actual tar/filetime license texts.
The Linux archive contains the tracked release notes, not untracked local
`docs/analysis` documents or temporary/cache fixtures.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `msync-0.6.6-linux-x86_64.tar.gz` | 7273287 | `590012d4a3a9e8665a9b9d99c685eb061db7e69ca6bffcd5b082c733e58c35aa` |
| `msync-0.6.6-windows-x86_64-setup.exe` | 6559286 | `1cf9db9b9c27a5a36a3911170367e6faa193344cbae600150c14113ab1b59b89` |
| `msync-0.6.6-windows-x86_64.zip` | 7229480 | `c4a056c7d814498bcc4ae61fc377be5cbd1859c0b499ce7c5f0650c9f210761e` |
| `msync-language-0.6.6.vsix` | 122090 | `7a9bbad21b0a0b5939b59472bef0f86feb6f1824b385fdbf41c9a25a40dc3757` |
| `SHA256SUMS` | 390 | `bf68ff64b3352ccd2ab86ae925163cb0df381069bfa76ab0eaa947e346907a09` |

### Separate Rust workflow is not green

The [independent Rust workflow](https://github.com/kleedaisuki/msync/actions/runs/41285893351)
failed on both Ubuntu and Windows. It was not a publication dependency, and
package acceptance above must not be described as full Rust compatibility.
The logged named failures number 29 on Ubuntu and 44 on Windows. No release
tag or asset has been moved/replaced to conceal this result.

Inspected failures include assertions for deliberately removed contracts:
insert-only container collisions, recovery registry creation, immutable-source
snapshots, readonly-bit refusals, path whitespace refusal, capacity refusal and
pairwise FIFO waves. Other failures concern actual diagnostic operand locations,
telemetry parentage/schema, Windows path/test-root spelling, and logical graph
comparisons. They have not all been individually attributed; do not classify
the entire failing workflow as stale tests or restore defensive machinery just
to make it green. For example, `ordinary_failures_retain_the_semantic_macro_operand_not_the_whole_call`
and `successful_physical_transfer_retains_task_context_across_polling` need
focused follow-up against their real diagnostic/observation contracts.

Reproduce an individual failure using the tagged source and
`cargo test --locked --test TARGET NAME -- --nocapture`, then check the actual
CLI journey and current contract before changing either code or expectations.
The remote logs and source are retained at the linked run; local raw log copy
is `.temp/release-v066/rust-failures.log` while available.

## v0.7.1 Rust CI fixture attribution (2026-11-03)

The [v0.7.1 Rust run](https://github.com/kleedaisuki/msync/actions/runs/41595828315)
stopped at three failures before reaching the selected integration journeys.
They are individually attributed below; this is not a blanket attribution of
other historical failures.

| Failure | Mechanism and classification | Correction |
| --- | --- | --- |
| `native_fs::tests::local_filesystem_lifecycle_and_endpoint_values` on Windows | Test setup writes a filename containing LF before calling the operation under test. Win32 rejects control characters 1–31 in an ordinary filename. Fixture problem, not a native filesystem dispatcher failure. | Retain spaces, single quotes, shell-looking text and Unicode on both platforms; retain the LF filename on Unix only. No production pathname restrictions are added. |
| `ordinary::tests::a_shared_capture_root_excludes_other_live_workers_without_hiding_user_scratch` on Windows | The owned root uses `filesystem::absolute`, which canonicalizes paths. Rust's Windows canonicalization produces extended-length syntax; `strip_prefix` against the noncanonical temporary directory fails despite matching directory identity. The test had already captured the source and found user `.temp` data before its own unwrap failed. Fixture spelling problem, not evidence of leaked private payload. | Form the relative-path oracle against the canonical ancestor. Capture through both the original and canonical source spellings, requiring user data to remain visible and the invocation subtree to remain excluded. Runtime ownership stays identity-based. |
| `object_export_never_replaces_its_source_or_a_readonly_output` on Linux | The test still requires a readonly-bit refusal explicitly removed from `write_object` by `1b5d91c7c5cb007494d5dc317911c3d10d4e4050`. Native rename in a writable directory can replace an unwritable file. Stale contract assertion, not a missing admission check. | Keep source-preservation and ordinary object-replacement assertions. Add an actual CLI compile/recompile/run journey comparing publication with native sibling rename on the same filesystem; require readonly metadata preservation, exact old bytes on native rejection, executable new bytes on success, and no remaining owned stage. |

Relevant primary platform references are
[Microsoft filename rules](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file),
[Rust canonicalization](https://doc.rust-lang.org/std/fs/fn.canonicalize.html), and
[Rust rename semantics](https://doc.rust-lang.org/std/fs/fn.rename.html).
The existing [normal-user tooling follow-up](safety-cost-audit-tooling.md#implemented-normal-user-follow-up-2026-11-01)
and the actual removal commit establish the project's contract; an old test name
does not restore a superseded refusal policy.

Local focused runs of the two library cases, the revised binary case and
`artifact_cli_v4::readonly_object_publication_matches_native_replacement_and_preserves_permissions`
passed. This fix changes test fixtures/oracles only: no production control flow,
permissions, publication or invocation-root behavior is changed.
