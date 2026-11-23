# HPIR product CLI: ordered containers

2026-11-22 · Linux x64 Native AOT. Original
[application corpus](../../tests/Performance/Corpus/README.md) sources,
O2, seed 17. The B+ inventory initializes 768 keys and applies 3,072
mixed lookup, upsert, and delete actions, followed by ordered size and
range probes. Set processes 8,192 events and 4,096 membership/expiration
queries, retaining a sorted membership snapshot across mutations.

The broad Set contrast keeps source, seed, and operation count fixed
but changes the key distribution and expiration mix: 7,226 distinct
insertions instead of 2,154. It exercises a larger live tree and
snapshot, not another vote for one aggregated speedup.

## Scope and measurement

The baseline is the [published 0.9.3 binary](hpir-0.9.3-baseline.md)
(`9edc10c0…`), on its own O2 artifacts. The candidate is the product
CLI Native AOT snapshot `4c216c3b…` (SDK 10.0.403), not the older
[pipeline gate's wire-5 snapshot](hpir-live-pipelines.md).

A minimal internal source producer uses the unlinked Core, Compiler,
Hir and Optimization DLLs from the exact v6 CLI AOT publish; it emits
fresh development format-6 HPIR objects. The public CLI binds the object,
identifies the bound image as HPIR, and runs it from another directory.
The source's compile-time path is absent before bind and run, and all
repeated binds produce the independently frozen bytes. Public source
production has not switched in this snapshot. No previous development
image or managed executor participates in the measurements.

Five AB/BA pairs after one warmup, CPU 6, telemetry off. Every execution
matches an independent Python oracle byte for byte. CPU is subprocess
user plus system CPU; wall includes process startup. The timing lanes
are separate:

- `.mexe`: already-bound artifact load and execute.
- `.mso`: public portable load/bind/execute, including its actual
  preparation and runtime policy; it is not `bind-ir` plus `.mexe`.
- `bind-ir`: the CLI reads the object, binds and writes the executable,
  but does not execute it.

| Source and distribution | Published → HPIR bound CPU p50 | Published → HPIR bound wall p50 | Paired CPU: bound | Paired CPU: object | Paired CPU: bind command |
| --- | ---: | ---: | ---: | ---: | ---: |
| B+ inventory, reference | 2,981 → 313 ms | 3,058 → 314 ms | −89.5% | −90.7% | −79.7% |
| Set, reference | 525 → 122 ms | 536 → 129 ms | −77.6% | −76.0% | −69.0% |
| Set, broad expiration | 989 → 220 ms | 995 → 222 ms | −76.8% | −78.0% | −66.4% |

The bind-only CPU medians were 201.8 → 41.0 ms (B+), 67.7 → 22.4 ms
(Set), and 73.3 → 25.2 ms (broad Set). The B+ portable lane had
noisy host wall time (about 7.75 s released wall with 4.02 s CPU);
do not generalize that wall ratio. Its paired CPU result and the
separately timed bound result point in the same direction.

On **identical HPIR bound bytes and the same CLI**, switching the
physical interpreter to adaptive execution saves 45.2% (B+) and
51.0% (reference Set) paired CPU. The physical interpreter medians
themselves were 626 ms and 292 ms, materially below the old bound
route on this host even without their optional native promotion
(these are independent timing lanes, not a pooled comparison).

## What the binder and runtime actually do

In separate profile runs, the old `prepare` span was 184 ms for B+
and 51.5 ms for Set. Its `backend.number_analysis` alone covered 204
and 118 functions (91.9 and 25.0 ms accumulated span durations).
The corresponding HPIR context-preparation spans were 86 μs and
63 μs. HPIR still reads and validates the encoded image: the B+
decoder visits 13,745 physical instructions and 1,803 call records,
and the Set decoder visits 3,051 and 395. No old number, liveness, or
inlining analysis runs during binding. The timing table measures the
whole commands, not a subtraction of overlapping profile spans.

| Runtime observation | B+ reference | Set reference | Set broad |
| --- | ---: | ---: | ---: |
| Published generated-native guest calls | 96,462 | 363,449 | 534,660 |
| HPIR adaptive generated-native guest calls | 712,415 | 545,427 | 850,512 |
| HPIR adaptive native entries | 1,845,831 | 519,533 | 882,126 |
| HPIR logical `math.floor` calls / inlined in machine phase | 39,403 / 37,749 | 4,096 / 4,096 | 4,096 / 4,096 |
| HPIR `HasLayout` call-site hits (all execution phases) | 1,960,101 | 476,502 | 813,879 |
| Process managed bytes in profile, published → adaptive | 274.0 → 3.43 MB | 60.8 → 5.73 MB | 103.4 → 5.75 MB |

B+ performs one input read, Set exactly 12,288 in each distribution
on both runtimes. HPIR executes more counted instructions than the
old IR; the two counts are not an instruction-throughput denominator.
HPIR routinely executes concrete byte addressing and scalar copies,
e.g. B+ has 4.27 million `OffsetPointer` hits. Both HPIR modes agree in
logical instruction counts, allocation and call-site counts. Remaining
guest allocations, reachability, and callbacks are real: the Set
contrast increases the HPIR collector's traced edges from 46,113 to
218,335 while keeping one guest stack segment. Managed process
allocation is a separate profile metric, not total physical memory.

### The remaining boundary is a layout check, not a guest call

The leading hot B+ site is the `_Chain::at` traversal at
[`containers.msync:681`](../../stdlib/containers.msync#L681).
The same execution of its `_Chain::node` / `node::next` expressions
issues three layout-service calls, each observed 257,095 times:
771,285 hits (39% of all B+ layout calls) at those expressions alone.
Set's hot sites include `_set_child` reading `current::left` (130,070),
`_set_key` reading `node::key` across two specialized bodies (126,950),
and the insertion loop's `index::nil` (73,012). They test the same
Set node layout in these images; broad Set increases the same work.

Some tests implement structural casts or access over multiple legal
view layouts; deleting them globally would change source behavior.
First keep the operation but remove the host boundary: the current
[`HpirHeap.HasLayout`](../../src/Msync.Runtime/Hpir/HpirHeap.cs)
is exactly a null test and an equality test of the live object's
layout word (8 bytes before the payload on this ABI). Selection
can implement that operation inside the same native stream, like
ready scalar Math, while retaining original site telemetry and the
same physical interpreter. Then address redundant checks in typed
producer graphs by preserving known layout across their links and
parameters, without assuming that arbitrary structural views are
monomorphic. The table establishes the size of the boundary, not
an unmeasured speedup for either next step.

## Direct native layout checks on the same images

The follow-up isolates the
[`InlineService` selector](../../src/Msync.Runtime/Hpir/HpirMachineImage.X64.Services.cs).
Its two Linux x64 Native AOT probe builds share one Core assembly, the
same Runtime source snapshot, the same adaptive policy, and the CLI's
redirected input implementation. In the control the selector declines
`HasLayout` emission and still inlines the same ready Math operations.
No producer, artifact, guest object layout, or interpretation change
participates: both probes run the exact three previously frozen v6
bound images, with the same independent oracle.

Every source-attributed logical import, guest call, opcode, instruction,
and edge count matches. The actual native reentries removed equal the
newly inlined layout checks exactly:

| Workload | Native entries before → after | Dynamic `HasLayout` inline hits | Direct guest calls, unchanged |
| --- | ---: | ---: | ---: |
| B+ reference | 1,845,831 → 31,747 | 1,814,084 | 712,415 |
| Set reference | 519,533 → 57,246 | 462,287 | 545,427 |
| Set broad | 882,126 → 82,796 | 799,330 | 850,512 |

This isolates a transition cost. It neither skips real layout
comparisons nor hides them from telemetry. The calls performed in the
pre-promotion interpreter prefix still use the same helper. Guest
allocations and collections are unchanged in all three paired profiles.

Separate uninstrumented timings on CPU 6 (seven AB/BA pairs after one
warmup) show the whole-workload effect:

| Workload | CPU p50 before → after | Paired CPU change | Paired wall change |
| --- | ---: | ---: | ---: |
| B+ reference | 253.89 → 99.25 ms | −59.9% | −59.7% |
| Set reference | 127.99 → 71.53 ms | −41.4% | −40.9% |
| Set broad | 199.16 → 114.41 ms | −43.1% | −42.5% |

This is a narrow controlled execution probe, not another product CLI
comparison against the published release. Its separate startup path
and timing window mean these ratios must not be multiplied into the
earlier release comparisons.

The [platform checks](hpir-platform-gate.md) record independent
development-mode correctness. This experiment additionally freezes
the exact Native AOT CLI, its matched producer inputs, detached
images, raw pairs, and independent profiled and diagnostic runs in
`artifacts/benchmarks/hpir-cli-ordered-containers/`. The controlled
layout selection payloads and separate raw receipts are in its
`layout-inline/` subdirectory. This does not cover public source mode
or non-x64 hosts.

## Compiler-only snapshot-header prototype (wire 7)

This follow-up isolates a representation choice rather than another
release comparison. The control is the coherent 18:03 UTC wire-7 tree;
the prototype differs in three compiler files only. It gives homogeneous
snapshot literals the same `[length, data]` header as materialized
snapshots. Literal payload starts at byte 16 in the **same** physical
allocation; its data pointer is an interior address, not a second owner,
additional allocation, tracked edge or generic Range tag. Constant
literal indexing still uses a direct displacement. The prototype also
allows known Number/String materialization to seed concrete field
carrier selection. Both versions produced new O2 objects from the same
original sources, and the same Linux x64 Native AOT product CLI bound
and ran all six images. Every run matched the original independent
stdout, including every timed sample.

| Workload | CPU p50 before → prototype | Median paired CPU change | Median paired wall change | Physical instruction count before → prototype |
| --- | ---: | ---: | ---: | ---: |
| B+ reference | 132.59 → 128.12 ms | −6.90% | −1.50% | 28,178,636 → 28,176,159 |
| Set reference | 96.25 → 90.94 ms | −3.65% | −4.23% | 15,473,641 → 15,473,644 |
| Set broad expiration | 148.39 → 147.78 ms | +0.03% | −0.06% | 24,088,205 → 24,088,207 |

Ten uninstrumented alternating AB/BA pairs on logical CPU 6 after one
warmup per arm, `MSYNC_JIT=auto`. Individual paired CPU deltas ranged
from −12.45% to +25.70% (B+), −32.51% to +14.44% (Set reference), and
−11.13% to +11.98% (Set broad). In independent instrumented runs, all
logical host-service counts, guest heap payload and object totals and
collector counts matched. B+ lost 2,478 `ConstU64` hits and gained one
`OffsetPointer`; the Set programs changed by only two or three
instructions. No systematic whole-program penalty is visible here, but
the small timing deltas are not an attributed speedup. These programs
barely exercise the changed path and do not measure sustained dynamic
reads through that header.

The unchanged snapshot-cursor journey still fails prototype selection
at `at(self::items, self::position)`: matching object headers do not
establish the concrete snapshot carrier of a loaded field. The useful
next gate is that actual cloned Cursor, including a live Null item and
mutable position, and a workload that repeatedly indexes a concrete
captured snapshot. The ordered pilot supports continuing that
simplification experiment; it does not justify covering unknown Range
with a runtime representation test or adopting the remaining field
analysis wholesale. The [isolated patch, artifacts and replay
instructions](../../artifacts/benchmarks/hpir-cli-ordered-containers/snapshot-header/REPLAY.md)
are retained separately from release results.
