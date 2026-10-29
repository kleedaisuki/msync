# Barrier execution validation (v0.5)

## Contract and acceptance scope

This replaces the transaction-era acceptance model. The authoritative requested
model is an ordered sequence of basic blocks: compile and graph optimization are
pure; the interpreter contextualizes only the current block after its predecessor
has completed. Ordinary finite filesystem statements have recoverable crash
atomicity. External programs and streams are not rolled back. `async` is a
before-statement fence, `await` is an after-statement fence, and a bare program
call has both. Explicit `await Program` joins that process. A macro joins its
remaining locally launched asynchronous programs before returning.

Normal cancellation drains external programs to their own exit rather than
truncating them. SIGKILL, operating-system failure, power loss and external
program-owned side effects are not covered by that lifecycle promise. Tests
must not turn a nonzero, self-selected exit code into an engine cancellation.

The previous release's green transaction/native-view tests prove only the old
contract. They are not evidence that the new execution path works. In particular,
in-process publication compensation does not prove crash recovery, and running
an old installed executable does not validate current unbuilt source.

## Requirement-to-evidence matrix

The state column records the strongest scoped gate obtained so far; it does not
claim a final release. Detailed executable generations, hashes and failures appear
below. A test name alone is not passing evidence, and Windows results do not
substitute for native Linux or real transport acceptance.

| Requirement | Authoritative evidence | Planned owner / artifact | State |
| --- | --- | --- | --- |
| No transaction syntax or implementation | Parser rejects transaction/commit/rollback; source/exports no MVCC/Manager/Isolation; no hidden feature-gated old runtime | Root removal audit; graph tests | Parser rejection passed; final removal audit pending |
| Pure compilation / export / preview | `check`, `compile`, `plan`, dry-run succeed with unusable transport helpers and nonexistent operands; trace contains no runtime inspect/execute spans | Existing static CLI regression + CLI tests | Generation-4 Windows CLI passed; final-source CI pending |
| Ordered basic blocks | Actual HIR partition and proptest over directional barriers; no optimized edge or rewrite crosses a block | `barrier_graph_v5` | Generation-4 Windows graph/property gate passed |
| Bare program call is synchronous | Child creates source, next statement copies its actual completed bytes; native process uses no Sandboxie/bwrap | `barrier_cli_v5` | Windows CLI creation/copy journey passed |
| Runtime sees newly created/deleted files | Actual child mutation followed by next-block copy/failure; no advance filesystem resolution | CLI/runtime journeys | Generation-5 Windows workspace passed |
| Async launch admits following work | Deterministic child handshake observes later independent finite write before release/join | `barrier_runtime_v5` | Generation-5 Windows gated runtime passed |
| Await fences future work | Child completion marker exists before following block evaluates; no speculative observable publication | Runtime journeys | Generation-5 Windows gated runtime passed |
| Macro return joins local launches | Child writes final marker before caller's next statement; prior explicit await avoids duplicate join | Graph + runtime journeys | Generation-5 Windows gated runtime passed |
| Retained stdout/stderr do not deadlock | Child writes more than pipe capacity, exits, then ports copy exact payload; output drained concurrently with wait | CLI/runtime journeys | Generation-5 ConPTY/output gate passed |
| Process PID and exit code are usable | Read actual typed ports and compare child PID/self-chosen exit code; no fabricated lifecycle state | CLI journeys | Windows CLI PID passed; generation-5 independent root exit passed |
| Normal cancellation preserves child lifetime | Cancel request occurs while gated child is alive; release child, observe final marker and normal drained output | Runtime journeys | Generation-5 real ConPTY and runtime passed |
| One flow EOF is local | One peer closes while another flow/child remains functional; process waits for remaining owned work | Runtime journeys | Generation-5 Windows physical/runtime passed |
| Bounded within-block scheduling | Gate/counter proves overlap for independent ready nodes and upper bound under configured limits; jobs=1 serial control | Runtime + graph journeys | Generation-5 Windows flow co-admission and capacity controls passed; Linux pending |
| Finite statement crash atomicity | Child process terminates at durable protocol failpoints; next invocation recovers a whole old/new tree, no mixed contents; earlier statements retained | Storage direct tests + `barrier_crash_v5` independent CLI tests | Generation-5 Windows 20 restart journeys passed; Linux/power-boundary review separate |
| Explicit destination modes remain correct | Existing directory exact rejection, trailing `/` container, `/.` replacement, selector all-or-none, symlink/readonly/alias controls | Migrated ordinary/filesystem tests | Windows ordinary/filesystem regressions passed; final-source full gate pending |
| Real remote transports remain correct | Isolated WSL SSH upload/download/recursive/overwrite journeys without modifying existing SSH configuration | Existing isolated transport suite | WSL generation-4 passed; repaired helper and isolated SSH gate pending |
| Export is executable typed IR | Mutate typed operation while debug source is unchanged; execution follows IR, not reparsed debug text; invalid references/relocations rejected | Object team + independent integrity tests | Artifact team's Windows 27-case gate passed; final-source CI pending |
| Human presentation stays useful | Colored help, script-in-place fmt, execution vs syntax diagnostics, persistent REPL commands and bindings | CLI team and retained UI/PTY tests | Windows CLI generation-4 and actual ConPTY generation-5 passed |
| New path performance | Re-run owned user macro fixture after correctness gate; compile invokes zero remote helpers; no implicit checkpoints; measured source/hash stated | Root performance gate | Pending correctness gate and measured optimization round |

## Test organization and resource discipline

Independent validation owns new `tests/barrier_*_v5.rs` files. Four leaf owners
cover CLI programs, deterministic runtime ordering, graph properties, and crash
recovery. They do not modify production code. Storage's direct recovery tests are
separate from independent CLI restart tests.

Local Cargo compilation is serialized by the root coordinator. Test binaries may
be executed directly once the matching build is complete. A timeout polling an
existing live process is not permission to start a duplicate Cargo build. Jobs
are bounded; temporary fixtures live beneath the repository `.temp` directory.
CI must run real native program tests normally on Windows and Linux, without
installing a transaction isolation driver or claiming that dependency presence
proves execution. Old transaction-only tests must be removed or migrated rather
than ignored or hidden behind a feature.

## Known pre-migration gaps

Inspection of `src/transaction/filesystem.rs` before the storage rewrite found
only in-memory publication phase booleans and retained rename journals; its module
contract explicitly excluded crash recovery. That cannot satisfy the new crash
atomicity requirement. The new crash tests therefore require a durable manifest,
recovery entry point and externally controllable test failpoints before they can
be considered meaningful.

## Migration checkpoint (2026-10-29, source only)

Independent new suites are source-ready but unbuilt: `barrier_cli_v5` has eight
real CLI/REPL journeys plus its child fixture; `barrier_runtime_v5` has gated
ordering/cancellation/flow journeys; `barrier_graph_v5` has seven targeted cases
and two 96-case properties; `barrier_crash_v5` contains the file/tree restart
matrix. Storage also owns separate direct protocol crash tests.

Applicable old coverage was preserved rather than dropping entire mixed suites:
`filesystem_v5` retains all fifteen former filesystem snapshot/publication tests;
`program_execution_v4` retains thirteen process/port/stream tests;
`macro_native_v4` retains the typed program-argument and capture journey;
`lowering_v4` retains nineteen lexical/typed lowering tests; `hir_plan_v4` retains
finite/FIFO/HTTP hazards and independent order-model properties. Pipeline,
submission, workspace, physical endpoint, source-obligation, ordinary copy and
metadata/hardlink cases were migrated. Obsolete Manager/store/named-origin undo
suites and signed-driver provisioning were removed. The ordinary large UTF-8
REPL case from the named suite now lives in `barrier_cli_v5`.

The user explicitly accepted startup recovery for multi-path/tree publication:
whole-old or whole-new after recovery, not instantaneous multi-path namespace
atomicity between a process crash and restart. This resolves the storage scope
question; single-file publication should still use the platform's atomic replace
operation when available.

The rewritten `scripts/test-ci-select.ps1` was executed successfully. It checks
focused filesystem recovery, block graph, runtime, direct native process, object,
CLI and deleted-target selections. The Windows/Linux workflow no longer installs
Sandboxie or bubblewrap; normal process E2E tests must run in the ordinary test
matrix. This script result proves selection logic only, not Rust correctness.

### Integration findings requiring actual regression runs

- Completion scalar reads must not wait inline before admitting independent
  same-block file work. The gated child waits for a marker that a later disjoint
  file node creates; its `exit_code` copy appears earlier in the same block.
- A background file-to-program-stdin flow retains a **read** obligation on its
  source until source acquisition/completion. Tracking only a destination path
  can allow a later file replacement to race the background source open.
- Interactive `submit` must not wait indefinitely for a full stream budget when
  the running flow needs a subsequent REPL action. Reject the new admission
  cleanly while preserving existing work; batch execution may queue safely.
- FIFO rendezvous admission must reserve the compatible cohort rather than
  consume its last slot on one side and wait forever for the peer.

These were routed to runtime owners. Their test sources or design intent are
not evidence that a production fix has passed; record actual execution later.

## First executable gate: generation 2

The root's selected no-run build completed and provided
`.temp/barrier-second-executables.json`. Independent direct test execution used
one test process at a time with `--test-threads=1` (no additional Cargo process).
Results are saved in `.temp/barrier-independent-results.json`; exact image hashes
are in `.temp/barrier-independent-generation2-images.json`.

| Target | Observed result | Interpretation |
| --- | --- | --- |
| `barrier_cli_v5` | 8 passed, child fixture ignored | Actual source CLI/REPL process creation/deletion, macro join, large stdout/stderr, nonzero exit, pipeline and read-after-write journeys passed. |
| `barrier_runtime_v5` | 7 passed, child fixture ignored | Deterministic fences, macro joins, natural cancellation, local EOF, independent scalar scheduling and retained stdin source bytes passed. |
| `block_specialization_v5` | 3 passed | Current-block creation/deletion and future-block observation boundary passed. |
| `barrier_graph_v5` | 7 passed, 2 failed | Two stale expected-shape assertions still assumed redundant `After` and combined transfer/join blocks. The actual bare-vs-explicit equivalence assertion passed. Source expectations were corrected and a separate exact three-block user example added; these changes require the next build. |
| `barrier_crash_v5` | 0 passed, 3 failed | No requested crash hook was reached: DOS-form test selector differed from the actual verbatim Windows target path. This run proves no crash atomicity. The harness now canonicalizes the target parent; next-build matrix remains required. |

A separate manual probe against the same generation-2 CLI confirmed the corrected
verbatim selector reaches `prepared` and exits 86. A following normal invocation
recovers and cleans its owned coordinator/record. That is one actual crash/restart
probe, not the complete crash matrix. The empty-source/empty-object restart
regression and real terminal Ctrl-C journey are also pending their next build.

## Generation-3 Windows executable gate

The next selected no-run build provided
`.temp/barrier-third-executables.json`. Independent tests again ran sequentially,
with one test process and no Cargo process. CLI SHA-256 was
`FF10743375195469DDBE4F6A27DC923FCD07065EF35BBA3D9DDC8C99048F03FA`;
per-image hashes and results are saved in
`.temp/barrier-independent-generation3-images.json` and
`.temp/barrier-independent-results-gen3.json`.

| Target | Observed result | What it establishes / remaining work |
| --- | --- | --- |
| `barrier_graph_v5` | 10 passed | Corrected HIR expectations, exact three-block user example, directional fences, macro joins, transaction rejection and graph partition properties passed. |
| `barrier_runtime_v5` | 7 passed, fixture ignored | Real Windows block/port scheduling, flow source read obligation, natural cancellation and local EOF regressions passed. |
| `barrier_crash_v5` | 3 passed | All 20 file/tree/existing/missing/failpoint/empty-source/empty-object restart journeys passed, including earlier-statement preservation and coordinator/index cleanup. This proves process-crash recovery, not a simulated power cut. |
| `barrier_cli_v5` | 8 passed, fixture ignored | All native CLI/REPL journeys passed again. |
| `barrier_terminal_v5` | Platform host passed; user journey failed after natural child completion | Real ConPTY Ctrl-C acknowledgement and child final marker survived. The next port export correctly returned background `submitted`, which the test incorrectly treated as failure. The test now performs `/wait`; complete 2 MiB capture, exit-code and reuse checks await another build. |
| `physical_execution_v4` | 5 passed, 3 failed | Batch stream capacity queueing, interactive capacity preservation and real named-pipe co-admission were not yet accepted. The named-pipe timeout was later diagnosed as an early finite-filesystem probe consuming the pipe connection and applying an invalid case-policy query. The fixture now fails fast with the real error rather than masking it as a timeout. Runtime fixes remain to be rerun. |

The root also ran a newer-source native Linux/WSL gate: graph passed, while the
scalar-independent-work regression failed and physical execution timed out.
The earlier Windows result does not override those later failures or establish
cross-platform correctness. Preserve the deterministic marker and FIFO tests
while owners repair the actual scheduling/refinement behavior.

### Live storage checks after durable-stage registration

Completed ordinary payloads must be gone, but a live session now retains an
identity-checked stage marker and its locked recovery registration. Tests use
read-only runtime observations `private_root()` and `private_stage_record()`;
they never grant themselves cleanup authority or ignore an `execute-*` prefix.
The shared helper permits only the exact root identity, its sole marker, and
its exact live registration (verified by a conflicting nonblocking file lock).
It does not read an exclusively locked Windows record through another handle.
After shutdown, neither root nor registration is permitted. Persistent local
`.lock` and remote ownership-marker files are checked exactly; arbitrary records
and staged payloads are still failures.

Additional independent optimization cases cover a 32-write run surrounded by
unrelated prefix/suffix operations (three publications), and the same overwrite
separated by an explicit barrier (two publications). The root's generation-3
ordinary test log contains both passing cases. Remaining ordinary parent-mtime
and live-metadata assertion failures were routed separately rather than used to
weaken payload-retention checks.

## Generation-4 Windows executable gate

The selected build completed in 64 seconds and supplied
`.temp/barrier-fourth-executables.json` (33 executables). CLI SHA-256:
`FF0125956FC0DB622DB40F31103E52671D7C4C0F62DCCF6B77FFE7C7D28EE32D`.
Independent tests ran one executable at a time with `--test-threads=1`; image
hashes and results are retained in `.temp/barrier-independent-generation4-images.json`
and `.temp/barrier-independent-results-gen4.json`.

| Target | Actual result |
| --- | --- |
| `physical_execution_v4` | 8 passed, including the real Windows named-pipe bridge, bounded batch queue and interactive refusal preserving existing flow. |
| `barrier_runtime_v5` | 7 passed; child fixture ignored. |
| `barrier_terminal_v5` | 2 passed; child fixture ignored. This includes actual ConPTY Ctrl-C, immediate drain acknowledgement, natural child completion, exact 2 MiB retained stdout, exit status and a fresh REPL assignment. |
| `barrier_graph_v5` | 10 passed, including the exact three-block example and generated graph partition cases. |
| `barrier_crash_v5` | 3 passed covering all 20 crash/restart combinations. |
| `process_exit_observation_v5` | 1 passed; fixture ignored. Root exit 7 became observable before releasing a descendant that still held stdout open; EOF and final child output were then collected. |
| `execution_workspace_v4` | 17 passed; three child fixtures ignored. Exact live-stage metadata, sparse enrollment, user scratch, ownership identity and parent-mtime controls passed. |
| `hir_plan_v4` | 33 passed, including file/FIFO/HTTP hazards, model properties and named-pipe namespace separation. |
| `version_v4` | 5 passed. |
| `recovery_concurrency_v5` | 0 passed, 2 failed: initial stream connection timed out before Session B or dead-record recovery was reached. No shared-registry concurrency claim follows from this run. |

The new shared-registry test intentionally uses a canonical Windows context.
A direct generation-4 CLI probe confirmed the actual defect: a verbatim native
context joined with a forward-slash DSL suffix fails with Windows error 123
before opening TCP, whereas the equivalent all-backslash path connects,
transfers and exits successfully. Probe artifacts live in
`.temp/concurrency-connect-probe/`. The test retains canonical-context coverage
and now polls the owned background result while awaiting a connection, exposing
the real error instead of waiting twenty seconds. Replacing that context with a
DOS-path workaround would hide the defect. The shared-registry part remains
unverified until the path fix is rebuilt and both sessions actually run.

The earlier native Linux tmpfs gate had **10 graph passes, 6/7 runtime passes**,
then physical execution timed out after 120 seconds during the independent-flow
journey. Its remaining physical cases were unverified. Windows generation-4
success does not substitute for rerunning those Linux paths on the repaired
source.

### Remote publication review after generation 4

Review found a real user-data hazard in a fixed remote parent lock filename:
opening `.msync-publication.lock` with shell output redirection truncates a
pre-existing user file and creates an observable artifact. The selected repair
uses an open directory descriptor for the parent lock, removing the filename
special case entirely. Upstream [util-linux flock(1)](https://man7.org/linux/man-pages/man1/flock.1.html)
documents directory locks and descriptor-based locking; NFS/CIFS limitations
remain explicit capability concerns. The remote owner applied the repair after
the generation-4 build and ran a native-ext4 helper probe preserving seeded
user-file bytes/mode/mtime/inode and keeping an absent lock filename absent.
That helper probe is useful evidence, but the changed production constants still
need the next Rust/CLI/WSL gate; generation-4 remote results predate this repair.

## Generation-5 Windows executable gate

The selected build completed in 52.41 seconds and supplied
`.temp/barrier-fifth-executables.json`. CLI SHA-256:
`02AFF035972A273D4550DE699B338574AE8396ADF98FA8192922D76C606D0D18`.
The actual runtime test executable's listing included **both** the new second
async-launch regression and the standalone-`await` physical-flow regression;
these were not assumed present from source modification times.

One independent test process ran at a time with `--test-threads=1`, without
Cargo or rebuilding. All seven assigned suites passed: **42 tests passed,
zero failed, seven subprocess fixtures ignored by the outer harness**. Those
fixtures are explicitly invoked as real child processes by the journeys.

| Target | Passed | Scope of direct evidence |
| --- | ---: | --- |
| `physical_execution_v4` | 8 | Actual TCP directions/EOF, immutable file source, bounded batch queue, interactive admission preserving old work, real dual named-pipe bridge. |
| `barrier_runtime_v5` | 9 | Directional fences, macro joins, scalar independence, retained file-to-stdin bytes, natural cancellation, local EOF; additionally async B starts while A's stdout remains open, and standalone await drains physical flows before future file statements. |
| `recovery_concurrency_v5` | 2 | Canonical Windows context now opens the real flow. Session B copies while A is alive; a dead Stage is recovered while A's exact root/marker/locked record survives. User control data and parent mtime remain intact; A's complete two-chunk payload is preserved. |
| `barrier_terminal_v5` | 2 | Real ConPTY Ctrl-C, actual acknowledgement, child natural completion without a signal-ignore fixture, retained exact 2 MiB output, exit code and subsequent REPL usability. |
| `process_exit_observation_v5` | 1 | Root exit status 7 is observable before a descendant closes inherited stdout; msync still owns output through final EOF. |
| `execution_workspace_v4` | 17 | Sparse/active-flow behavior, scratch user data, strict live metadata, identity exclusions, program-created paths and directory metadata controls. |
| `barrier_crash_v5` | 3 | All twenty process-crash/restart cases, including empty/declarations-only source and objects, whole-tree state and completed earlier statements. |

The standalone-await regression uses a **real progress join event**, not an
arbitrary timing absence: with TCP EOF held, the runtime must enter its join
phase while the following marker is absent. If await were skipped, that phase
would occur only in final session draining after marker publication. EOF is then
released and both output and marker must complete.

Evidence artifacts:

- `.temp/barrier-independent-results-gen5.json`
- `.temp/barrier-independent-generation5-images.json`
- `.temp/barrier-independent-<target>-gen5.log`

This is a Windows selected-executable gate, not a claim of complete release
acceptance. Repaired-source native Linux FIFO/Unix-socket journeys, isolated SSH,
full-source CI/lint, performance-after-correctness and the final installed artifact
remain separate gates owned by the root coordinator. Earlier failed generations
remain documented above to preserve the actual causal history; they do not
contradict the newer targeted Windows results or replace those remaining gates.

## Next Linux gate harness preparation (2026-10-29)

The project-local `.temp/barrier-wsl-tests.sh` now separates `build`, `run`, and
`all` phases so the coordinator can serialize Cargo without rebuilding while
other owners execute a frozen image. The selected targets are physical endpoint,
barrier runtime, barrier graph, native program, durable crash, stage crash,
recovery registry, selected acquisition, filesystem, and ordinary execution.
The build records a validated target-to-executable TSV map; run records SHA-256
for every test executable and the matching CLI, bounds each suite to 240 seconds
plus a 15-second termination grace, and preserves any failure across later suites.

Build inputs and artifacts remain on the existing DrvFS checkout. Functional
fixtures are a private 256 MiB Linux tmpfs mounted over the checkout's `.temp`
inside isolated user, mount, network, and PID namespaces. This is a functional
Linux-filesystem gate, not an ext4 throughput measurement. Loopback is enabled
only inside the private network namespace; no SSH or WSL configuration changes
are required.

After setup, the harness drops all capability bounding, inheritable, and ambient
sets before executing tests. Namespace UID 0 alone is not evidence of ordinary
DAC enforcement: a separate actual probe on 2026-10-29 printed effective
capabilities `0000000000000000` and could not read an owned mode-000 file
(`DAC_PROBE_ENFORCED`). The same capability assertion and unreadable-file probe
are mandatory at every suite run. Selected suites do not themselves require
mount/user-namespace privileges. `bash -n` passed for the updated harness.
No new Rust build or Rust test result is claimed by this preparation entry;
source-owner freeze and coordinator authorization remain prerequisites.

## Generation-6 actual Linux functional gate

After the coordinator froze generation-6 production sources, the authorized
`barrier-wsl-tests.sh all` run built the ten selected integration binaries in
55.46 seconds. Session 69589 reached terminal exit 1, not a polling timeout.
The exact executable map is `.temp/barrier-wsl-test-binaries.tsv`; stdout and
stderr are `.temp/barrier-wsl-gen6.log` and `.temp/barrier-wsl-gen6.stderr`.
The CLI SHA-256 was
`8f44eef2fe8eaa4aaba9d9a6bd8d3d5b9955de9984946990f3ffcfa1b9e746f0`.

Fixtures actually reported tmpfs, effective capabilities all zero, and
`DAC_PROBE_ENFORCED`. These results exercise native Linux FIFO/socket/permission
semantics; they are not DrvFS endpoint results or ext4 throughput measurements.

| Target | Passed | Failed | Ignored fixture children |
| --- | ---: | ---: | ---: |
| physical_execution_v4 | 18 | 3 | 0 |
| barrier_runtime_v5 | 9 | 0 | 1 |
| barrier_graph_v5 | 10 | 0 | 0 |
| program_execution_v4 | 13 | 0 | 1 |
| storage_crash_v5 | 4 | 0 | 1 |
| storage_stage_crash_v5 | 1 | 0 | 1 |
| recovery_registry_v5 | 22 | 0 | 0 |
| selected_capture_v5 | 6 | 0 | 0 |
| filesystem_v5 | 13 | 0 | 0 |
| ordinary_execution_v4 | 15 | 3 | 0 |

Total: 111 passed, six failed, four child fixtures intentionally ignored by the
parent harness. The ordinary user macro and two selector journeys failed during
publication: the staged regular-file target was `new/config.toml/` or
`new/keep.txt/`, producing Linux `EISDIR` (21). Sparse selected acquisition itself
passed, including unselected FIFO, symlink, and DAC-unreadable directory cases.
The readonly interrupted-directory-mode recovery case also passed.

The three physical failures require owner fixes or an explicitly justified
fixture repair, not silent reruns: (1) recreated FIFO generation correctly raised
an identity error but lacked the required constructive recovery hint; (2) a FIFO
cohort with an external TCP prerequisite never admitted the TCP connection and
hit the fixture's bounded accept timeout; (3) submitting a directory to a Unix
socket returned success with zero assignments instead of rejecting before
workspace creation/connection. All failures were reported to the root coordinator
with exact logs. No production source was changed by the validator.

### Generation-6 Windows contrast and unresolved status

The root coordinator's direct Windows logs were inspected separately from the
Linux run. `.temp/barrier-root-<target>-gen6.log` records the following all-green
scoped results: library 130 passed/six ignored, REPL jobs 16 passed, barrier runtime
nine passed/one fixture ignored, graph ten passed, and Windows physical endpoint
eight passed. The corresponding Windows CLI hash recorded in
`.temp/barrier-runtime-gen6-images.json` is
`914A0B641E8A7B85844B0E6277E0BFE016082323F0058F796A0ABE39F2D19186`.
These are different binaries and platform paths from the Linux hashes above.

**All six generation-6 Linux failures remain unresolved at this checkpoint.**
Storage and runtime owners have received the exact reproductions. Windows green
does not waive the Linux-only file-path, FIFO/cohort, socket-preflight, or
constructive-diagnostic defects. The correctness gate is not yet green and the
performance-after-correctness gate has not opened. The next authorized Linux
run should rebuild after owner source freeze and repeat the same ten suites,
retaining the current failing logs rather than overwriting them or rerunning
until one pass appears.

## Generation-7 actual Linux gate: all eleven suites passed

The coordinator authorized a fresh source build after the six Linux findings
were repaired. Session 14610 completed with exit 0; compilation took 47.25
seconds. This run added the **complete library unit suite**, not just the ten
integration suites, to validate Linux-only remote materialization and retained
acquisition ownership. Its exact build manifest and executable map are preserved
as `.temp/barrier-wsl-tests-build-gen7.jsonl` and
`.temp/barrier-wsl-test-binaries-gen7.tsv`; stdout/stderr are
`.temp/barrier-wsl-gen7.log` and `.temp/barrier-wsl-gen7.stderr`.

CLI SHA-256:
`fe402abf1a458fbbb878dc49a7e1f42858369551e6ad5c52f0f0592597374419`.
Library test executable SHA-256:
`8958c83bb3428ba1fcbc69783fd3f868cd1aa8fcda662b5ad43ccba6d9d0a216`.
Every other test binary hash is recorded before suite execution in stdout.

The harness again observed source/build `v9fs`, functional endpoint **tmpfs**,
`CapEff: 0000000000000000`, and a successful unreadable-file DAC enforcement
probe. Compilation on the checkout does not turn these into DrvFS endpoint
results; likewise tmpfs functionality is not an ext4 throughput benchmark.

| Target | Passed | Failed | Ignored |
| --- | ---: | ---: | ---: |
| Complete library unit suite | 140 | 0 | 2 |
| physical_execution_v4 | 21 | 0 | 0 |
| barrier_runtime_v5 | 9 | 0 | 1 |
| barrier_graph_v5 | 10 | 0 | 0 |
| program_execution_v4 | 13 | 0 | 1 |
| storage_crash_v5 | 4 | 0 | 1 |
| storage_stage_crash_v5 | 1 | 0 | 1 |
| recovery_registry_v5 | 22 | 0 | 0 |
| selected_capture_v5 | 6 | 0 | 0 |
| filesystem_v5 | 14 | 0 | 0 |
| ordinary_execution_v4 | 18 | 0 | 0 |

Total: **258 passed, zero failed**. The six ignored outer-harness entries are
two explicitly release-only controlled performance experiments and four fixture
entrypoints invoked by their real parent journeys. They do not hide failed
functional tests. Expected caught-panic telemetry tests emit panic text under
`--nocapture` but both tests completed successfully; their stderr is not a test
failure.

All six previous generation-6 Linux findings now have actual passing regression
evidence on the new build: canonical assembled manifest paths publish regular
files without an empty-root trailing separator; selectors and the exact user
config macro publish successfully; recreated FIFO generations get the required
constructive rejection; FIFO cohorts admit their external TCP prerequisite;
and directory-to-Unix-socket submission rejects before connection/workspace
creation. Existing strict assertions were retained. The new canonical manifest
regression also passed.

The full Linux library suite additionally passed actual literal and regular-file
remote-material root tests, unselected-symlink/no-match material acquisition,
retained private-root/registry exclusions, and the dropped-submit/new-submission
reused-ID regression. This closes the six scoped Linux blockers documented
above. It is still a selected correctness gate, not the complete CI/release,
measured optimization round, or installed-artifact acceptance claim.

## Windows generation-7 full target sweep and generation-8 repairs

The three `.temp/barrier-full-gen7-{0,1,2}-results.json` manifests and their
actual test-result lines were inspected: **all 73 integration targets executed,
601 passed, nine failed, 46 ignored**. The separate generation-7 library and
binary suites passed 136 and 22 tests respectively; five debug benchmark
executables skipped their timing workloads and do not constitute performance
evidence. The nine failures spanned six targets: immutable proof sharing, join
HIR shape, preparation sibling-resource expectations, terminal progress,
runtime task telemetry, and independent phase diagnostics.

Generation-8 rebuilt repairs have passing targeted evidence: proof sharing 1,
join 10, preparation 35, and independent phase journeys 4. The last phase fixture
was corrected to require the precise quoted literal token `"missing"` at byte
range **57..66**, not the entire `$pwd / "missing"` path expression at 50..66;
`.temp/barrier-phase-gen8-final.log` records all four passing after that correction.
The earlier failed phase log is retained, not silently reclassified. The CLI
owner's `.temp/barrier-cli-results-gen8.json` and four corresponding logs record
another **30 passing tests**: terminal 9, task telemetry 3, REPL jobs 16, real
ConPTY cancellation 2. Their CLI SHA-256 is
`5E13804B734422D2E59201FD6C09ED7196D72727EA31E587232888FBED09B202`.
Thus all nine known generation-7 failures now have rebuilt passing regressions;
this is not a claim that the complete 73-target sweep ran again on generation 8.

Generation-8 strict all-target Clippy completed in 11.43 seconds and formatting
passed. Its doc-test command passed **zero test cases**, which proves the command
completed but adds no broad behavioral coverage. Actual Linux generation-7
results remain the separately identified 258-pass gate above: that Linux image
predates the final shared telemetry/proof-Arc refinements, so it must not be
misrepresented as a generation-8 Linux run. Full CI, the measured performance
optimization round, and installed-artifact acceptance remain pending.
