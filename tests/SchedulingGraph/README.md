# Resource scheduling contracts

The default entry copies real sibling files and checks affine prefix-index
visits, actual payloads, RAW/WAR order and selected native observations.

`--retirement-only` exercises completed work through explicit acquire and parent
directory observations. The 1/100/1000-copy comparison asserts exact affine
resource visits, not a wall-clock threshold. Source compilation, serialized
relocatable bytecode and serialized bound bytecode separately exercise discarded
failure, consumed `false`, earlier successful publications and corrected retry.
A small actual-index model additionally checks late lease retirement after a
same-path node is replaced, plus retained fault/cancellation dependencies.

`--retirement-observe` records the same frontiers without asserting linearity;
it is for comparing the pre-fix implementation, not for accepting it.

`--namespace-only` exercises native directory-link publication, retransmission,
retargeting, obstruction/retry and future namespace WAR through all three CLI
stages. These remain independent of completed-frontier retirement.

## Contextual I/O backend

`--contextual-io-only` checks real source compilation and serialized `.mso` /
prepared `.mexe` execution. Two discarded writes to the same destination must
admit only the surviving operation. A read of the first destination version must
receive that version, whether by retaining its producer or by safe source-version
forwarding. A consumed producer Bool still requires its operation. Exact payloads
and actual admission counts distinguish the cases; the copy-only observer does
not demand three physical copies when two operations preserve its result. A
final correct destination alone does not prove redundant I/O disappeared.

The CLI-level companion is
`tests/Msync.EndToEnd/contextual_io_journey.py --binary /absolute/path/to/msync`.
It covers directory-namespace reads, directory merges with surviving earlier
members, a directory produced before a regex selection, frozen `.mexe` selected
members across repeated submission, and actual destination-obstruction failure
followed by retry of the unchanged artifact. Preparing a plan must not publish
files. These are execution contracts, not stale-plan validation or automatic
replanning tests. `--case` and `--route` select the affected journey directly.

The focused count check also allows dead-write elimination across plain acquire
or release when the permitted direction can fuse the work; it does not treat
both markers as opaque bidirectional fences. A demanded final transfer Bool does
not keep an earlier unobserved overwrite alive. An empty finite selection returns
true, admits zero file operations and does not create an output directory.

The CLI fixture additionally distinguishes publication of a final symlink entry
from mutation of its referent, computes a known metadata branch before export,
and runs one terminating mutable loop through two calls with different Number
arguments and a changed captured String prefix. It checks the actual distinct
output paths and bytes; its timeout is an external test watchdog, not a language
iteration quota. The `planned-repl-io` selector in `repl_state_journey.py` covers
already-executed submission prefixes and changed exported Path values.

The published 0.8.1 Linux baseline (executable SHA256
`105e29503c74422831c8481281c617deba5356eb56f38ec9da3b23fbd89c5732`)
opens both source files for the two-write motivating case in each of the three
routes. A functional `strace openat` probe records one content-read open per
source; these observations are not latency measurements. Its `.mexe` regex
selection also includes a new matching file created after binding, so the new
frozen-member test distinguishes real prepared work from the old late VM
selection. Disposable baseline records are under `.temp/backend-acceptance/`.

`--plan-batch-failure-only` puts an unobserved successful copy and an observed
failing final copy in the same local frontier with Jobs=1. The observed Bool is
false, the successful publication remains, and the failure retains its concrete
diagnostic/count; the batch owner must not reinterpret that handled result as a
failure of its unobserved sibling.

`--plan-versions-only` prepares 40 short generated regular-file programs and
compares their public plans with an independent sequential file-version model.
Eight seeded valid topological schedules per program check RAW/WAR/WAW repair
and dead-writer liveness. Only four real fixture files are created; execution of
the prepared nodes is modeled in memory, and those physical files must remain
unchanged. Real directory, link and error behavior remains in the CLI fixtures.

`--plan-prepare-observe` isolates contextual preparation for 1/128/1024
independent copies. Source compilation, binding and fixture creation are outside
timing. Three warmups and five retained samples report preparation milliseconds,
current-thread managed allocation traffic, actual plan nodes/batches and residual
instruction/block counts. It does not execute the VM, make a Native throughput
claim or impose a CI threshold. Use a coordinated short quiet window and retain
the common managed assembly hashes from its JSON output.
