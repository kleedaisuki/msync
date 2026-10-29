# Statement crash recovery acceptance

The finite-statement contract is tested at process boundaries. The writer exits
without unwinding at deterministic publication cuts. A different process recovers
the destination parent, and a third recovery proves idempotence. Existing files,
new files, nested directories and empty directories are compared by content and
shape, not by summary counters.

## Required outcomes

| Writer cut | Outcome after recovery |
| --- | --- |
| Prepared private replacement | Entire old statement state |
| First target moved to before-image | Entire old statement state |
| First target installed, another target pending | Entire old statement state |
| All targets installed, durable decision absent | Entire old statement state |
| Durable commit decision present | Entire new statement state |

An initially absent target must become absent again when recovering an
uncommitted statement. A committed tree must retain its empty directories and
must not retain removed old children. Recovery repeated after completion must
not recreate before-images or alter committed values.

This tests recoverable atomicity. It does not claim that an unrelated process
reading multiple paths during publication observes a filesystem-wide atomic
snapshot. Atomic rename of one entry cannot provide that stronger guarantee.
The recovery discovery scope and lock must match the production runtime; a test
that merely calls an in-memory rollback receipt does not prove crash recovery.

## Execution status

### Windows selected build, generation 3 (2026-10-29)

The root's unified `cargo test --no-run` build completed, then the storage team
ran its returned executables directly and sequentially with `--test-threads=1`.
The executable map was `.temp/barrier-third-executables.json`; the storage result
manifest, including SHA-256 hashes, is `.temp/barrier-storage-results-gen3.json`.

| Target | Actual result |
| --- | --- |
| `storage_crash_v5` | 3 passed, 1 child-fixture test ignored by the harness |
| `recovery_registry_v5` | 11 passed |
| library `execution::ordinary` filter | 12 passed |
| `execution_v4` | 14 passed, 1 explicit WSL test not selected |
| `ordinary_execution_v4` | 13 passed, 2 failed |
| `execution_workspace_v4` | 7 passed, 10 failed, 3 explicit external-environment cases not selected |

The crash executable hash was
`DBD868D204007F182D7F0D0422649C8F01687D34B219E4AF7C69552605E55027`.
The ordinary guard tests include positive source rotation and negative missing,
readonly, target-alias, erased traversal and post-stage mutation cases. All six
guard tests passed after removing redundant whole-program preparation from the
statement-local proof path.

This is not release acceptance. The broader failures identify assumptions about
an intentionally live session invocation versus retained payloads, plus an old
parent-directory timestamp expectation. The chosen new contract preserves
selected portable metadata and unrelated siblings but lets parent namespace
mtime follow actual atomic publication; restoring an old parent timestamp would
erase concurrent independent namespace activity. The test now checks the
unrelated sibling's identity, bytes, permissions and mtime instead. This source
adjustment and strict live-owner fixture updates still require another run.
Their actual generation-3 logs are
`.temp/barrier-storage-ordinary_execution_v4-gen3.log` and
`.temp/barrier-storage-execution_workspace_v4-gen3.log`. Later source changes and
Unix-only permission recovery still require a fresh build and execution; the
generation-3 results do not validate unbuilt patches or Linux behavior.

Power-loss persistence additionally depends on the documented platform and
filesystem sync contract; process exit alone cannot simulate loss of volatile
device caches.

## Interrupted private staging

`storage_stage_crash_v5` runs a fresh writer with the production stage crash hook
at `stage-materializing`, scoped to its exact fixture destination. The source has
two ordered files. Exit status 86 is required, and the private stage must contain
the first copied file but not the last. Its recovery registry record must already
exist. Thus the test distinguishes interrupted materialization from an exit
before acquisition or after complete staging.

Two fresh recovery processes must preserve the existing public target, remove
the identity-registered private stage and its record, and preserve a separate
user-owned directory with the same `.msync-publication-` prefix. Prefix matching
alone is not an ownership proof. Its actual execution evidence is recorded below.

### Windows selected build, generation 4 (2026-10-29)

The root's fourth unified no-run build finished successfully in 64 seconds. The
storage team used the resulting compiler-artifact executable paths, ran one test
process at a time with `--test-threads=1`, and recorded hashes and exit statuses in
`.temp/barrier-storage-results-gen4.json`.

| Target | Actual result |
| --- | --- |
| `storage_crash_v5` | 3 passed; its ignored child fixture was invoked by the harness |
| `storage_stage_crash_v5` | 1 passed; its ignored child fixture was invoked by the harness |
| `recovery_registry_v5` | 18 passed |
| `ordinary_execution_v4` | 15 passed |
| `execution_v4` | 14 passed; 1 optional actual-WSL journey not selected |
| library `execution::ordinary` filter | 15 passed |

All 66 selected tests passed. This includes the repaired generation-3 failures,
the final-literal metadata-flow negative, two simultaneously live worker stores
under one outer capture root, refusal to replace active backing ancestors,
marker/physical-identity stage cleanup, and recovery of a dead stage alongside a
live ownership record.

The interrupted-staging executable hash was
`80131C55587F44FB6ACC353A750E7F0FA9142CE019DA0524E5F05024ECAD03C8`.
Its actual log is `.temp/barrier-storage-storage_stage_crash_v5-gen4.log`.
The subprocess reached the first copied file, exited 86 without unwinding,
and fresh recovery removed the registered partial stage while preserving both
the old target and the unregistered same-prefix user directory.

These local Windows results do not establish Unix-only directory-permission
crash repair. The WSL projection coverage was executed separately below;
neither platform behavior is inferred from a cross-platform compile check.

### Actual Windows/WSL projection, generation 4

The explicit `wsl_projection_v5 --ignored --test-threads=1 --nocapture` target
passed all three actual-WSL journeys in 9.57 seconds. The fixture used project-local
Windows files, translated their paths with the installed guest's `wslpath`, and
checked that the guest filesystem type was not ext4. This deliberately exercises
the projected/DrvFS fallback, not the separate native-ext4 bulk path or an IO
throughput benchmark.

- A projected guest path to the same Windows file was rejected without mutation.
- A projected directory could not publish into its own descendant.
- A projected source containing configured scratch retained user data and
  same-prefix user directories, but excluded the actual owned invocation and
  recovery registry by identity.

The executable hash was
`192F4580D1BE832C9F5661D250E53E172BF39C112C1D29D2555D744FA9979221`.
Evidence is `.temp/barrier-storage-wsl_projection_v5-gen4.log` and
`.temp/barrier-storage-wsl_projection_v5-results-gen4.json`. This adds functional
coverage for the fallback's native identity, overlap and private-view proofs;
it does not substitute for native-Linux permission recovery or performance tests.

### Selected acquisition helper probe (2026-10-29)

The selected-acquisition shell protocol was exercised directly in
`Ubuntu-24.04`, with the source on native ext4 under `/tmp` and the exclusively
owned import beneath project `.temp` on `/mnt/d`. The root agent independently
reran `.temp/wsl-selected-leaf/run.sh` and observed exit 0 with
`PASS WSL selected-only, percent basename, no-match, selected-FIFO rejection`.
The probe checked selected file bytes and a complete directory subtree with a
percent-sign basename, exclusion of an unreadable secret tree/FIFO/symlink,
empty sparse acquisition, and selected-FIFO refusal before import creation.

This is a functional shell-helper probe, not an IO benchmark or proof that the
Rust integration has run. `selected_capture_v5`, the added ordinary selector
journeys, the WSL acquisition unit/ignored test, and the complete DSL WSL selector
journey in `remote_publication_v5` still require execution against the next built
source generation before their acceptance is recorded.

### Selected acquisition and storage regressions, generation 6

The serialized generation-6 Windows build produced CLI SHA256
`914A0B641E8A7B85844B0E6277E0BFE016082323F0058F796A0ABE39F2D19186`.
The directly executed storage runner completed successfully with 46 passing tests:

| Target | Result |
| --- | --- |
| `selected_capture_v5` | 4 passed |
| `ordinary_execution_v4` | 16 passed |
| `recovery_registry_v5` | 21 passed |
| `storage_crash_v5` | 3 passed; crash child invoked by harness |
| `storage_stage_crash_v5` | 1 passed; crash child invoked by harness |
| library exact real WSL selected acquisition | 1 passed in 15.16 seconds |

The actual WSL test acquired chosen bytes and the full selected subtree from
native ext4 while leaving the unreadable secret tree, FIFO and symlink out of the
private import; no-match and selected-FIFO refusal also passed. The library image
SHA256 was `FE1380FD4FEF9C9F1F6376D40E005F2C0388F236F18F2801565836D651181D98`.
Per-target image hashes, durations and logs are recorded in
`.temp/barrier-storage-results-gen6.json`; all images remained unchanged during
the runner.

The separate complete DSL WSL selector test reached all content, selected-count,
no-match and selected-special-file assertions, but its final generic fixture
cleanup scan failed by recursively visiting the deliberately mode-000 unselected
directory. The test now verifies that this directory still has mode 000, restores
fixture-only traversal permission, then runs the generic journal scan. That
test-only correction is not in generation 6 and requires a rebuilt exact-test
rerun; the full DSL test is not counted as passing above. This result does not
cover the two Unix-only sparse-capture tests or the Unix ordinary special-file
journey; those require the native Linux functional gate.

### Generation 7 publication-key and full WSL selector verification

Generation 7 CLI SHA256 was
`3CC4ECBB59B24CDE4013C2CF265D9FE1A93A9DA17CDA1E24F30C3B0D35FE424B`.
The 25-target Windows chunk completed with 198 passing tests, one failing graph
allocation-sharing regression, and 17 intentionally ignored environment/fixture
tests. Its storage targets all passed: `filesystem_v5` 14, ordinary execution 16,
recovery registry 21, and sparse selected capture 4. The new assembled-manifest
test checks raw canonical key spelling as well as actual file publication, so
Windows pathname tolerance cannot hide a reintroduced `file/` key.

The exact complete DSL native-ext4 WSL selector test also passed (1/1, 2.85 seconds),
including complete selected subtree data, unreadable/special/non-UTF-8 unselected
entries, no-match no publication, and rejection of a selected FIFO. It confirms
the generation-6 test-only cleanup correction against a rebuilt image. Test image
SHA256 was `0C957091949E4B438F35FAEE2DF4342C63A5F555A9B3CB07664547F5E921C8D2`.
Evidence is `.temp/barrier-full-gen7-0-results.json` and
`.temp/barrier-storage-wsl-selector-dsl-results-gen7.json`. Test/CLI hashes remained
unchanged during both runs. The isolated Linux gate must separately establish
that canonical manifest paths repair the Unix EISDIR failures; a Windows pass
does not stand in for that result.
