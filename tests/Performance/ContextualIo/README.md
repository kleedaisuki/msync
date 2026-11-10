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
