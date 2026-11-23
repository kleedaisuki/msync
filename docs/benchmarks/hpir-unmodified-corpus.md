# HPIR on original signal and polygon workloads

2026-11-22 · Linux x64 Native AOT development runner. This is the original,
unmodified [application corpus](../../tests/Performance/Corpus/README.md),
not the ready-input derivative in the [native-block pilot](hpir-machine-blocks.md).
Seed 17/reference, standard rows (signal 65,536; polygon 16,384), O2.
The two inputs are read from real stdin in every measured invocation.

## Comparisons and provenance

Every route checked independent exact stdout, not simply agreement between
engines. Preparation ran outside the timer; source was removed before
binding and execution used an unrelated directory. Seven alternating
pairs after one warmup, one pinned vCPU (6), telemetry off. Child CPU is
user plus system CPU time; wall includes process startup, artifact load,
and native code emission when selected. The test runner is not the final
user-facing CLI.

- **Same HPIR:** identical bound image and one Native AOT executable,
  interpreted versus generated x64. This isolates execution mechanism.
- **Published bound:** independently built published Linux 0.9.3 versus
  the local HPIR AOT runner. They run their own bound images of the same
  source. This is the named release baseline.
- **Local bound control:** exact 0.9.3 tag rebuilt on this host with
  SDK 10.0.403. It executes the released baseline's same old bound bytes,
  not a separately prepared legacy artifact. This controls toolchain
  and packaging differences, not the actual 0.9.3 release denominator.
- **Published object control:** official 0.9.3 on its own portable image,
  without the old bound inlining/replanning regression. This is the
  existing effective fast path; do not pool it with bound timing.

See the [baseline provenance](hpir-0.9.3-baseline.md) for official and
same-host hashes. The pre-fix HPIR probe was `1a05a402…` (format 3). The
post-observation pre-Math-inline probe was `115be557…` (format 4).
A fresh runner is required across this wire version change: the old
HPIR reader rejects format 4, it does not silently execute it.
Each pair lane collected its own native samples.

## The original regression was pending-carrier work, not dispatch

The first original-workload pilot was noisy (unrelated builds began
after it started), but signal regressed materially even by child CPU
against both versions of the bound baseline. Polygon was near the
old bound baseline and materially behind the released object. Examples:

| Original pre-fix workload | Same HPIR interpreter → native CPU p50 | Published bound → HPIR native CPU p50 | Paired native CPU change | Local bound → HPIR native CPU p50 |
| --- | ---: | ---: | ---: | ---: |
| Signal | 1,840 → 1,698 ms | 897 → 1,923 ms | +122.9% | 1,008 → 2,348 ms |
| Polygon | 682 → 562 ms | 441 → 493 ms | +6.7% | 559 → 593 ms |

There were exactly two actual text reads in either program. The
early scalar carrier selection nevertheless made the input-dependent
loop a graph of future operations. Separate recorded executions used the
same bound images and returned exact output:

| Counter | Signal pre-fix | Signal after input observation | Polygon pre-fix | Polygon after input observation |
| --- | ---: | ---: | ---: | ---: |
| FutureLift | 1,680,095 | 0 | 926,425 | 0 |
| FutureMath | 196,608 | 0 | 32,768 | 0 |
| FutureDemand | 262,152 | 2 | 393,218 | 2 |
| Guest allocations | 1,873,121 | 52 | 959,225 | 30 |
| Guest collections | 74 | 2 | 236 | 1 |
| Native entries | 2,138,915 | 196,672 | 1,352,445 | 32,805 |
| Ready `math.floor` imports | 0 | 196,608 | 0 | 32,768 |
| Direct guest calls | 262,153 | 262,153 | 229,379 | 229,379 |

Signal LCG `draw` PCs 8/10/12 lifted multiplication, addition and
remainder on the carried state every row. The spike test at main
PCs 154/156/157 lifted and demanded the condition every row.
Detector `ingest.2` PCs 10/12/17/19/21 lifted the arithmetic and floor
at source line 20; this pattern ran in all three detectors. In polygon,
the two comparisons and equality at `crossed` PCs 11/13/15 (line 30)
accounted for 589,824 lifts alone. Native direct guest calls already
worked; per-operation future helper calls and their allocations
dominated instead. Profile rows attribute all sites back to the original
source.

Two diagnostic input-only variants discriminate the mechanism. Inserting
a real Number-demanding branch in `read_number` added two lifts and two
demands to each workload but left all loop lifts/GC. Supplying only the
two input values as ready literals, with the original Scope, Range,
math, and loop body unchanged, removed all future imports and GC.
Allocations dropped to 45/20. Neither source change is used for the
measured original-workload gate.

The producer fix
([`SelectObservedTextConversions`](../../src/Msync.Hir/Lowering/LlvmHirLowerer.TextObservation.cs))
keeps the earlier observation attached to the same text producer: a
demanded null test in function entry has already committed that exact
line, including when named through a nonescaping single-store local.
The subsequent Number conversion uses the observed path. No extra
demand, proof at load/bind, or blanket eager conversion is required.
On the unchanged programs this results in two actual text observations
and two ready Number parse calls. Genuine deferred inputs still use the
typed future model.

The independent legacy profiles show the old binding failure on these
same data: signal's `.mso` had 261,632 machine calls and 3 bridges, versus
zero machine calls and 327,043 bridges in the bound image; polygon's
machine calls fell 229,226 → 0. This is a different loss from HPIR's
pre-fix continuation of already-observed input as deferred computation.

## Post-observation, pre-Math-inline: quiet paired result

No build was observed in the before/after checks. All outputs were
correct, and both original programs beat official bound and optimized
object paths once separately chosen for HPIR native execution. The x64
emitter in this snapshot still calls a host helper for ready floor.

| Workload and lane | Before → HPIR native CPU p50 | Median paired CPU change | Before → HPIR native wall p50 |
| --- | ---: | ---: | ---: |
| Signal, same HPIR interpreter | 175.5 → 52.9 ms | −71.1% | 177.1 → 54.4 ms |
| Signal, published bound | 686.9 → 51.9 ms | −92.6% | 691.4 → 52.9 ms |
| Signal, exact-tag same-host bound | 726.1 → 45.0 ms | −93.9% | 729.1 → 46.2 ms |
| Signal, published object | 74.5 → 52.2 ms | −28.0% | 76.0 → 53.8 ms |
| Polygon, same HPIR interpreter | 90.2 → 18.0 ms | −79.0% | 93.9 → 19.2 ms |
| Polygon, published bound | 407.0 → 17.8 ms | −95.6% | 409.6 → 19.0 ms |
| Polygon, exact-tag same-host bound | 451.2 → 21.8 ms | −95.2% | 453.3 → 23.2 ms |
| Polygon, published object | 176.6 → 17.3 ms | −89.9% | 187.1 → 18.5 ms |

The remaining native exits were overwhelmingly ready floor calls.
Machine selection of a fixed, pure scalar ABI targets this operation
family on the same bound image while leaving language semantics, the
interpreter, and actual deferred calls unchanged.

## Ready scalar Math: make the real work native

To isolate the x64 selector, both Native AOT runners used the same
`Msync.Core.dll`, SDK, probe, and frozen runtime source. The one treatment
was [`ReadyMath`](../../src/Msync.Runtime/Hpir/HpirMachineImage.X64.Math.cs):
the control keeps the selected scalar operations as typed calls, while
the candidate emits x64 instructions. Compile/bind each unmodified
application once with the same producer, then run those exact bound
bytes in all four execution arms; no source changes or run-time edits.
The producer was refreshed because the development Text completion
layout changed after the previous snapshot. Both sets used development
format 4, but they do not have an interchangeable completion ABI. The
earlier archive is kept intact, not used as the Math control.

| Original workload | Native CPU p50 before → after | Paired native CPU change | Native wall p50 before → after | Physical entries before → after |
| --- | ---: | ---: | ---: | ---: |
| Signal, 65,536 rows | 49.23 → 26.50 ms | −50.3% | 51.35 → 27.71 ms | 196,672 → 63 |
| Polygon, 16,384 rows | 19.26 → 17.08 ms | −8.5% | 20.48 → 18.23 ms | 32,806 → 38 |

Independent stdout and the logical import census matched at each
function and HPIR PC across all four arms. Signal still performs 196,608
floor operations and 262,153 direct guest calls; polygon still performs
32,768 floor operations and 229,379 direct guest calls. The new
`machine_inline_calls` counter assigns every one of those floor
operations to generated instructions. The two cases have different
workload shares per helper transition, hence different whole-program
impact. The interpreter did not change algorithm in this A/B, but its
independent p50 CPU pairs moved +9.4% for signal and −1.4% for polygon.
The signal benefit is large; the smaller polygon effect is directional
on this virtual host. The elimination of physical exits is directly
counted rather than inferred from the timers.

The same after-runner and bound polygon image executed under QEMU user
mode 10.0.13 with exact output both on `Nehalem` and on
`Nehalem,-sse4.1`. With the SSE4.1 feature bit cleared, all 32,768 floor
operations used the existing typed helper; with it set, all were
inlined. Neither ISA choice changed the HPIR program.

Changing only stdin to 64 rows (the independent generator reproduced
identical source) makes eager native selection +7.5% and +4.9% paired
child CPU for signal and polygon, respectively, versus interpreter in
this after binary, with startup-dominated wall time. The same comparison
on the standard inputs saves 85.3% and 83.5% CPU. These numbers do not
measure in-process promotion; use them to motivate observed guest work
rather than a file-size or row-count admission rule.

**Policy gap:** Interpreter-only is not a universal performance
replacement: signal at ~175 ms is still substantially slower than
the old effective object route at ~75 ms (across separate pair lanes).
On the other hand, a blanket eager-native policy is unsupported by the
[short/long emission study](hpir-platform-gate.md). Admission should use
observed guest work or reusable compiled images, with the same HPIR
continuation and no old VM fallback. None of these tests constitutes
the final CLI/published package gate or covers the rest of the corpus.

Exact independent inputs, artifacts, code snapshots, benchmark drivers,
raw pairs, host receipts, and separate profiles are retained under
`artifacts/benchmarks/hpir-unmodified-corpus/`.
