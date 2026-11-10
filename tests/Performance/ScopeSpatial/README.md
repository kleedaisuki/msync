# Scope spatial-layout execution probe

The complementary `native_tail.py` runs fresh-process Native launch/source/mexe
controls with paired blocks and observed P95/P99 ranks. Its boundaries, sample
design and shared-host interpretation are documented in
[the tail-latency note](../../../docs/design/performance-tail-v081.md).

This probe compares immutable product assembly sets without rebuilding the
product. It compiles and binds each source outside the timer, then executes the
same bound program on both runtimes. Each source constructs escaping records in
native Buffer storage and scans their fields. Residual `MakeScope` and
`GetMember` sites are required; a constant-return artifact is not accepted as a
layout result.

The cases distinguish four different allocation regimes:

- `computed`: four freshly computed Number fields per record;
- `literals`: four literal Number fields whose immutable wrappers can already be
  shared by the baseline constant pool;
- `mixed`: two computed Number fields plus String and Path references;
- `references`: String, Path and Bool fields, a no-numeric-storage control.

All sums use exactly representable values at the selected sizes and are checked
bit-for-bit. The measured interval includes each new interpreter's object
construction, retained record scan and selected continuation preparation. It
excludes source compilation, initial binding and fixture setup. This is managed
component execution, not Native startup, filesystem throughput or a claim about
collected/retained heap size. `AllocatedBytes` uses the process-wide precise GC
allocation counter; it is construction traffic rather than retained memory.

Build a host for each immutable image, with isolated outputs:

```text
dotnet build tests/Performance/ScopeSpatial/Msync.ScopeSpatial.csproj -c Release -m:1 -nr:false -p:FrozenAssemblies=IMAGE -p:ArtifactsPath=OUTPUT
DOTNET_TieredCompilation=0 NO_COLOR=1 OUTPUT/bin/Msync.ScopeSpatial/release/msync-scope-spatial-probe --size 512 --warmups 3 --samples 5 --root .temp/scope-spatial/work --output REPORT
```

Use the same root, size, level, SDK/runtime and source across both images. Run
only after the semantic calibration succeeds and coordinate a short quiet
window; do not add CI timing thresholds. Reports preserve every sample,
assembly/source/artifact SHA256, residual site counts and framework settings.

## Managed spatial-layout control (2026-11-10)

The initial packed-storage image (`spatial-common-1`) increased allocation
traffic by 119,640 B for computed/literal rows and 82,792 B for mixed rows at
512 records. Independent actual-VM inspection confirmed packed rows, so this
was not a missing-construction-path result. Subsequent causal inspection found
that contextual `KnownLoop` expansion repeatedly materialized numeric wrappers
while interpreting a scan that had no contextual I/O to prepare. The changed
image (`spatial-common-3`) excludes such bodies from contextual expansion,
separates row-allocator ownership, and recognizes reused literal wrappers so
subsequent literal records retain ordinary storage.

One changed-image control used the same bound root, O2, 512 records, three
warmups and five measured interpreter executions per case. Both frozen hosts
used .NET 10.0.14 with `DOTNET_TieredCompilation=0`. Baseline was
`backend-common-8` (Core `26d5af4a`, Runtime `bd858dc6`); changed image was
`spatial-common-3` (Core `32029072`, Runtime `722b11d1`). Both test-only direct
reference hosts built without warnings or errors. Four 32-record functional
calibrations and all measured result-bit checks passed.

| Case | Baseline median ms | Changed median ms | Baseline allocated B | Changed allocated B |
|---|---:|---:|---:|---:|
| Computed numeric | 37.9711 | 54.7548 | 10,888,024 | 10,817,136 |
| Shared literal numeric | 39.8945 | 36.2277 | 10,838,720 | 10,747,192 |
| Mixed numeric/reference | 37.4445 | 73.6851 | 10,827,288 | 10,817,840 |
| Reference-only control | 37.9634 | 68.5884 | 10,687,680 | 10,669,104 |

Allocation traffic is stable within each case (except small background
increments in mixed/reference cases) and improves for all four changed cases.
This is the combined storage/preparation change, not an isolated packing
benefit. Latency does **not** establish a speedup: the reference-only control
also slowed sharply, despite not using numeric packing. Preserve these samples
rather than interpreting only the favorable literal result. This single
baseline-then-candidate batch is descriptive, not an interleaved or Native AOT
latency experiment, and does not establish whether the observed latency change
comes from the changed runtime or execution conditions.

Both images used identical source SHA256 and retained identical
MakeScope/GetMember/instruction counts: 3/10/659 (computed), 3/10/641 (literal),
3/9/650 (mixed), and 3/8/640 (reference). Encoded bound artifacts nevertheless
had different SHA256 values even with the same root. The reports retain this
difference; source/result/site equivalence must not be described as byte-for-byte
artifact equivalence.

Disposable raw reports are
`.temp/scope-spatial/baseline-common3-control-512.json` and
`.temp/scope-spatial/common3-512.json`. Each contains all samples, full assembly
hashes, source/artifact hashes and residual counts. The source generator and
commands above reproduce the workload without preserving these disposable
files.

### Bound artifact identity diagnosis

A subsequent **compile-only** reference fixture (four rows, identical synthetic
source name and bound root, no interpreter execution) compared both images by
exact encoded constant bytes, function signatures/types, context, instruction
operands and every source span. Constants, signatures/types, context, spans and
empty plan tables were identical. Of 132 instructions, 17 differed:

- Twelve module-construction positions reflected a different order of the
  `storage` module's field pushes and `MakeScope` field names. Matching pushes
  by their field name produced exactly the same payload for every field.
- Five `GetMember` instructions differed only in their logical-offset hint.
  The three `Buffer` selections matched each image's constructed module layout.
  The physical row layout remained `label/path/flag`: baseline hit `label` and
  fell back by name for `flag`; changed image fell back for `label` and hit
  `flag`. Both had one missed row hint per scan iteration.

The source of order variation is explicit in compiler code:
`SourceBinder.BindStandardModule` constructs export fields by enumerating
`ImmutableDictionary.Values`, while `SemanticAnalyzer.Types.Shape` derives
structural layouts from an `ImmutableDictionary`. Their string-hash-dependent
order is not a source-order contract. `MemberRead` computes a hint from that
semantic layout; runtime `ScopeOffset` checks the field name before falling
back to name lookup. Thus the SHA difference is actual internal layout/hint
variation, not different filesystem context or hidden source-span content. It
does not change this fixture's named values or scan operations, nor establish
a cause for the overall latency difference. Do not treat independent-process
artifact SHA equality as a prerequisite for this source-level control, or claim
that these artifacts are byte-identical.

The disposable diagnostic host and exact diff are under
`.temp/scope-spatial/artifact-delta-host` and
`.temp/scope-spatial/artifact-delta.diff`. No timing samples were added for this
diagnosis.

### Bounded sampled-thread attribution

One gated pair subsequently ran the **reference-only** 512-row source on the
same frozen images. A test-only host compiled/bound the module and completed
three warmups before printing `READY`; only then was `dotnet-trace` attached
with `dotnet-sampled-thread-time,gc-collect`. Releasing the input gate executed
40 result-checked interpreter calls. This was attribution of existing work, not
a new timing matrix or product instrumentation. The installed collector's
noninteractive attach-ready output is `Output File`, not `Recording trace`;
an initial wait-only orchestration attempt timed out without releasing the
execution gate and contributed no workload samples.

| Attribution | Baseline | Changed |
|---|---:|---:|
| 40-call wall interval (ms) | 1,748.49 | 2,166.94 |
| Process CPU increment (ms) | 1,864.71 | 2,316.75 |
| Gen0 / Gen1 / Gen2 collections | 52 / 0 / 0 | 52 / 0 / 0 |
| Sampled thread stacks in inferred workload interval | 1,358 | 1,671 |
| Nearest Runtime frame: `Interpreter.IndexBlocks` | 1,264 (93.1%) | 1,581 (94.6%) |
| Known blocking-wait frames | 0 | 0 |
| `SuspendForGC` stopped interval total (ms) | 62.79 | 67.47 |

The process CPU counter measures all process threads during the host interval;
it is not exclusive method CPU time. Sample weights are wall-thread stack
observations, not formal CPU attribution. The test-only dynamic start/stop
marker events were not emitted at the selected provider level, so offline
analysis bounded samples by the first and last sampled stacks containing a
`Msync.Runtime` frame. Compilation and warmups preceded collector attachment;
gate waits and post-execution report work are excluded by that bound. The
inferred intervals were 1,744.80 and 2,160.67 ms, close to the explicit host
intervals. No recollection was performed to repair marker output.

Both profiles locate the same dominant mechanism:

```text
Interpreter.IndexBlocks
  BlockBuilder.ForFunction / Partition
    InstructionEffects.ScopeConstructionMayFail
      HashSet.AddIfNotPresent / Resize
      Array.Copy
    Enumerable.ToArray / SegmentedArrayBuilder.ToArray
      ReadOnlySpan.CopyTo
```

Many sampled top frames are `Thread.PollGC`: the profiler itself suspends
threads to sample them. `SuspendOther` events accounted for 195.43/255.78 ms
and must not be relabeled as garbage collection. The small `SuspendForGC`
component and absence of recognized blocking-wait stacks make resource waiting
or additional GC collections an unsupported explanation for the main cost.

The source mechanism is concrete: `Interpreter.RunOwnedCodeAsync` calls
`IndexBlocks` whenever preparation returns a different module. `IndexBlocks`
rebuilds the function's block partition and dense index. That partition scans
effects repeatedly; a large standard-module `MakeScope` re-splits field names
and constructs a duplicate-name `HashSet`. The workload's continuation count
therefore repeatedly pays for immutable function/effect/block facts. Reusing
those facts when `FunctionCode` is unchanged is a more direct optimization
target than changing object packing policy. This profile establishes that
shared dominant cost; it does not isolate a single cause for the baseline versus
changed-image latency difference.

Disposable attribution artifacts are under
`.temp/scope-spatial/thread-attribution/`: `host/Program.cs`, typed
`collect.py`, `reader/Program.cs`, `*-stdout.log`, `*-profile.json` and
`*.nettrace`. The reader uses existing TraceEvent assemblies from the installed
`dotnet-trace` tool, without product builds or instrumentation.

### Causal block/preparation fix control

After the sampled attribution, the changed image `spatial-common-4` retained
the spatial-storage/capture/affine slice and added the targeted block/effect
fix: cache the pure large-Scope field-name fact, partition with one linear
effect pass, construct the dense block index directly, retain unchanged
`FunctionCode` identity, and skip reindexing when that function is unchanged.
The image receipt identifies Core `2f06bebc` and Runtime `5c9e8a54` (full hashes
are in the raw report). No packing-policy variants were measured.

Both hosts used the same baseline and source/root/O2/512-row/3-warmup/5-sample
settings as above. Four 32-row calibration cases passed first; validation peers
completed their CPU tasks before the single quiet matched batch. Every measured
result passed and all source hashes and residual site/instruction counts matched.

| Case | Baseline median ms | Common4 median ms | Ratio | Baseline allocated B | Common4 allocated B |
|---|---:|---:|---:|---:|---:|
| Computed numeric | 52.8175 | 5.9721 | 8.84× | 10,888,024 | 1,807,744 |
| Shared literal numeric | 46.3339 | 4.5699 | 10.14× | 10,838,720 | 1,737,800 |
| Mixed numeric/reference | 48.9885 | 5.5814 | 8.78× | 10,827,288 | 1,795,440 |
| Reference-only control | 49.0049 | 4.4897 | 10.92× | 10,687,680 | 1,732,720 |

Allocated traffic decreased by 83.4–84.0%. This large improvement across both
numeric records **and the reference-only control** is evidence for removing
repeated block/preparation work, not an isolated claim that numeric packing
makes every record scan 9–11 times faster. The result is managed interpreter
component latency including selected preparation, not Native AOT startup or
filesystem throughput. Baseline-then-candidate ordering and five samples do not
support precise tail estimates, but every changed-image sample was substantially
below every corresponding baseline sample in this control. The earlier common1
allocation regression and common3 latency results remain above rather than
being discarded after the causal fix.

All samples and full assembly/source/artifact identities are retained in
`.temp/scope-spatial/baseline-common4-control-512.json` and
`.temp/scope-spatial/common4-512.json`. No additional profiling or instrumentation
was used for this changed-image batch.

## Same-Core spatial storage component

The execution control above combines several mechanisms. The separate
`Component/` host isolates the **current Core's ordinary versus packed Scope
storage** without an old-image comparison or product toggle. Both modes share
Core SHA256 `2f06bebcb27c3a687cf6691dd862e7bde90ec4fd6ed439f81d39c4004459818d`
and the same `ScopeLayout`. Each row is a fresh Scope initialized from an
identically generated `Value[]` containing four freshly allocated, non-shared
`NumberValue` inputs. Plain construction transfers that array directly; packed
construction uses one `ScopeAllocator` and copies numbers into numeric pages.

The single measured case retained 32,768 rows (1 MiB logical numeric payload),
scanned all four fields with `TryGetNumber` for 32 passes, warmed three times,
and collected five samples. Bit checks verify every initial field and every
scan sum. Test-only reflection outside timing verifies fresh identities,
packed-row count and numeric-page ownership. A 16-row calibration passed before
the quiet measured case. No profile or second working-set variant was run.

| Component measure | Plain | Packed |
|---|---:|---:|
| Median 32-pass scan (ms) | 29.9101 | 17.5746 |
| Scan allocation (every sample, B) | 0 | 0 |
| Construction allocation traffic (B) | 6,553,648 | 9,187,640 |
| Approximate retained managed bytes | 6,554,824 | 4,206,072 |
| Distinct Scope identities | 32,768 | 32,768 |
| Actually packed rows | 0 | 32,768 |
| Numeric pages / capacity bytes | 0 / 0 | 136 / 1,056,736 |

Raw scan milliseconds were `[29.9101, 33.4086, 44.1179, 25.2327, 19.6667]`
(plain) and `[18.6698, 17.5746, 17.5462, 16.0234, 28.6322]` (packed). The
observed median ratio is 1.70×; it is not a hardware-cache-counter measurement
or a universal speedup. The held rows' approximate managed footprint fell by
35.8%. Retained bytes are `GC.GetTotalMemory(true)` deltas with live rows kept
reachable, measured outside timing, not exact object-size accounting or process
RSS. Each mode's rows are released before the next mode's initial full GC;
small shared reflection/runtime metadata can remain.

**The construction trade-off is material:** with identical boxed input arrays,
packed construction allocated 40.2% more traffic. Its pages outlive the input
wrappers/arrays, reducing retained heap, but creating those temporary inputs is
not free. The VM's raw-number construction entry avoids these particular input
boxes; this component deliberately does not grant that advantage to only one
mode. Conversely, shared literal wrappers are not represented by this workload
and must not be claimed as a packing win. Keep these component outcomes
separate from the much larger repeated-block-work fix above.

Reproduction uses only the frozen Core assembly:

```text
dotnet build tests/Performance/ScopeSpatial/Component/Msync.ScopeSpatial.Component.csproj -c Release -m:1 -nr:false -p:FrozenAssemblies=IMAGE -p:ArtifactsPath=OUTPUT
DOTNET_TieredCompilation=0 NO_COLOR=1 OUTPUT/bin/Msync.ScopeSpatial.Component/release/msync-scope-spatial-component --rows 32768 --passes 32 --warmups 3 --samples 5 --output REPORT
```

The disposable measured report is
`.temp/scope-spatial/component-common4-32768.json`; source, mechanism checks,
all samples and exact Core identity are reproducible from the host source.
