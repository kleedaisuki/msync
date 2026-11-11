# Contextual I/O user latency

`paired.py` measures one explicitly selected actual CLI `run` workload against
two immutable images. Do not compare a published Native image with a managed
apphost as a production speedup. Record the candidate build receipt alongside
the output; the harness captures both executable hashes and the exact generated
source hash itself.

```text
python3 tests/Performance/ContextualIo/paired.py \
  --baseline /absolute/path/to/published/msync \
  --candidate /absolute/path/to/candidate/msync \
  --case independent --count 128 --pairs 7 \
  --output .temp/contextual-io-perf/independent128.json
```

The independent case copies N distinct small files to N destinations. The
`overwrite` case targets one destination with N different source paths, so only
the final destination version survives under the new backend contract.
`selection` transfers one finite regex selection into a destination directory.
The `directory` case copies an ordinary directory tree, with nested 32-file
groups, an empty directory, distinct Unix executable/data modes and copied
file/directory modification timestamps. Every payload and those metadata
properties are checked outside timing. This distinguishes normal configuration-
folder synchronization from scalar statements or regex-only batching.
Select a workload and size relevant to the implementation change rather than
running a default matrix.

One shared working directory and the same existing output paths are used by both
images. Each invocation receives a fresh fixed-length generation in every source
file, outside timing. Every resulting filename and payload byte is verified
after timing, so a cached output or no-op cannot appear faster. There is one
untimed launch per image followed by alternating pairs, with every raw sample
retained and no performance threshold. Fixture generation and verification are
not timed; process startup, source compilation, contextual preparation, actual
I/O, output and exit are timed. These measurements are end-to-end latency, not
isolated VM or transfer-engine throughput.

Coordinate the short timing window with build/test/installation owners. Actual
admission and graph node counts are measured by the focused correctness tests,
not inferred from wall-clock variation. Remote connection/authentication counts
belong to the real loopback OpenSSH fixture and are not simulated here.

`--inspect-plan` optionally compiles, prepares and disassembles this exact source
after all timed samples. It records actual plan-node kinds, live transfer nodes,
eliminated transfers and batch counts for each image, plus the prepared artifact
hash and full graph JSON. An older image without graph metadata is marked
unavailable, not assigned fictional zero-cost graph work. Inspection commands
are outside timing and do not execute transfers.

## Managed preparation diagnostic: common4

The first isolated preparation observation uses immutable input SHA256
`dee5bd73febdaed1a9c6b231084e109036b6c889300967adccc592112979f4e4`,
Runtime SHA256
`587c19ba80656badc1c46361202084235f2ecf34e8f8b5e688548cc2d2d8cdbe`
and Core SHA256
`e96b856702be37937f486a368be3ebd39802179abb1405970e7edccfa5741272`.
The existing SchedulingGraph host's `--plan-prepare-observe` entry calls the
public backend, not an alternate graph builder. Debian 13, .NET 10.0.14,
`DOTNET_TieredCompilation=0`, three warmups and five raw samples per size:

| Independent transfers | Prepare median, ms | Managed allocation, bytes | Live nodes / batches | Residual instructions / blocks |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 0.2364 | 40,128 | 1 / 1 | 3 / 3 |
| 128 | 7.4909 | 2,657,560 | 128 / 1 | 3 / 3 |
| 1,024 | 84.0829 | 21,549,808 | 1,024 / 1 | 3 / 3 |

Source compilation, ordinary binding and fixture generation are excluded. Actual
filesystem metadata observation is part of preparation and is included. No VM
or transfer operation executes. Allocation is current-thread managed traffic,
not RSS or retained heap. The approximately 21 KB/file growth is roughly linear;
three sizes with timing variation do not prove quadratic construction. The
84 ms/21.55 MB ordinary-case cost warrants focused causal investigation before
claiming low optimizer overhead. This is not a Native user-latency comparison.

Raw samples and the exact assembly identities are under
`.temp/backend-acceptance/common4-preparation-cost.json`; the common producer
receipt is `.temp/backend-common-4/producer.json`. The build, test and transport
owners paused other development work during the measured window. No new broad
component matrix is warranted without an implementation change or a specific
attribution question.

One follow-up repeats only the changed 1,024-transfer case with immutable input
`9f934c0713680418713f65ee47a569aa9bbb5f1862fa5232714fbab2fbf5b5bf`
and Runtime SHA256
`327aa67b6de8efe36a4003fd4712a46ab60b30d51cbb0d3d34e8dbdc36d6e239`.
The same source hash, scratch-path shape, TieredCompilation=0, three warmups and
five samples allocate 12,255,272 bytes each, down 43.1%. Raw times are 82.6646,
82.7042, 78.1468, 39.0218 and 53.0479 ms (median 78.1468 ms); this variability
does not establish a robust timing win. The 1,024 nodes, one batch, three
instructions and three blocks remain unchanged. This is the combined source5
change, including span-based index traversal and parent/canonical-path caching,
not attribution to one isolated hunk. Raw observations are
`.temp/backend-acceptance/common5-preparation-1024.json`. No second trace or
three-size rerun was performed.

## Instantiated footprint index and graph construction

The graph consumes already selected, concrete footprints. Keep one inline
index entry at a single-definition path and allocate a set only when multiple
entries actually share that path; leaf paths do not allocate child maps.
Graph construction unions overlaps directly into a scratch set, and a
file-only frontier never materializes a namespace-change query. The remaining
namespace queries are existential, with the same reaching definitions as the
full projection. Copy forwarding uses exact identity to find a one-file
producer; it needs the hierarchical projection only to retire producers under
a subtree overwrite. This is a change in representation and the cost of asking
questions, not permission to remove any RAW, WAR, WAW, namespace, or observed
result dependencies. As in [MemorySSA's clobber walkers](https://llvm.org/docs/MemorySSA.html),
the query type determines how much information to collect; the planner does
not instantiate compiler memory effects as filesystem alias proofs.

An isolated managed replay of 1,024 independent local files compares base
`5f25145` with only these three Runtime/Planning changes. The same compiler,
Core and optimizer assemblies compiled and bound source outside the timer.
Each of four alternating AB/BA process pairs used five warmups and nine samples
per process (.NET 10.0.14, TieredCompilation=0, CPU 11). Both variants used
the same fixture, produced 1,024 nodes in one batch, and their serialized
`.mexe` bytes matched for each workload. No transfers occurred while timing.

| Actual source shape | Allocation per preparation | Pooled median elapsed |
| --- | ---: | ---: |
| 1,024 direct copies | 15,294,760 → 12,172,520 B (-20.4%) | 71.3 → 69.4 ms |
| 1,024 calls to a known copy helper | 16,314,256 → 13,691,728 B (-16.1%) | 90.3 → 70.8 ms |

Per-process medians varied substantially with concurrent host work, so neither
row establishes CLI throughput or tail improvement. The allocation reduction
and byte-identical graphs were repeatable. Raw observations, exact assemblies,
patch identity, and the source of the replay are under
`artifacts/benchmarks/library-matrix-v086/deep-plan-footprints/`. Reused
focused journeys covered version-liveness/320 topological executions, actual
helper calls through all three source/object/executable routes, read/result
observers, mixed failure, forwarding with/without source clobber, and future
directory-link aliases.
