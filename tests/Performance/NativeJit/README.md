# Native guest JIT acceptance

Run the real Native AOT CLI, not an interpreter library or the historical
standalone LLVM probe:

```sh
python3 tests/Performance/NativeJit/journeys.py .temp/current/msync \
  --work .temp/native-jit-acceptance
```

The first invocation is implicit source execution (`msync program.msync`).
The compiler then emits a portable object from the same source. The harness
moves only the object into a new directory and deletes the source before
executing that object, binding it, and executing the resulting image.

Each route checks a stdin-driven branching recurrence against a separate
Python binary64 oracle. The IEEE loop exercises division, all six comparisons,
NaN, negative infinity, fractional values, and both zero signs. Decimal results
are parsed back to binary64 and compared by their bit pattern, except that NaN
is checked by classification. A three-iteration cold case must not promote.

The normal telemetry-off invocation is checked separately. The `profile`
passes compare automatic JIT with `MSYNC_JIT=off`, assert actual native
compilation/entry/yield counters on supported hot paths, and compare the
original logical opcode counts per image/function. The `instructions` mode
uses the numeric interpreter on purpose; its canonical site/edge counts
must remain identical with or without the opt-out. The Linux shebang case
executes the source file as a program via `#!/usr/bin/env msync`, with native
counters checked in its SQLite profile. The Linux SIGINT probe waits for a
runtime marker in a trillion-iteration workload, checks that native execution
actually ran when supported, and expects a flushed cancelled execute span
and exit code 130.

The expected native hosts are Linux x86-64, Linux ARM64, and Windows x86-64.
Linux ARM64 requires the system `__clear_cache` helper and the kernel
sync-core membarrier command; a host lacking either runs the interpreter
instead. Unsupported hosts and `--expect-fallback` require zero native
counters, keeping numeric region and telemetry checks enabled. Use an
individual pre-CFG fixture if comparing with a binary whose numeric tier
still requires an empty stack. The cancellation signal test itself is
Linux-specific. No timing threshold is used.
SQLite databases and the JSON result remain under the selected
repository-root `.temp` directory.

The nested fixture deliberately runs fewer than 128 iterations per inner
invocation. Its full outer region needs a non-entry continuation: with
19 inner backedges and one outer backedge per round, the first 4,096-edge
fuel quantum expires at the inner header. The oracle checks completed
updates as well as the arithmetic result. The floating remainder fixture
checks the current backend's fallback for operations it does not emit.

The merge fixture retains the old Number below an `if` expression in
optimized source bytecode. The raw `carry-pair` fixture uses the actual
format-5 writer, not source lowering, to carry two Numbers across the loop
entry, backedge and exit. A duplicated Number remains live across a write
to its original local; the next pair is permuted, and the caller computes
`a - 3*b` outside the region. The independent integer oracle checks the
result and exact logical `Dup`, `Jump` and conditional counts.

The raw fixture generator uses the repository's .NET SDK and Core project;
the CLI being tested needs neither. Use `--fixture merge` or
`--fixture carry-pair` to select only the new region coverage.
A generated object can be supplied as `--raw-artifact path/to/carry-pair.mso`
to avoid rebuilding the test fixture. Only that object is given to the
isolated binder before portable and executable runs.

The effect-tail fixtures test straight-line native suffixes **after**
canonical dispatch. `effect-tail` verifies every `SetMember` write and
discards its unused Null result. `member-tail` uses the current Number
returned by a canonical `GetMember`. In the raw `tail-call` and
`tail-native` fixtures a source call or direct native call consumes exactly
one input line; the numeric tail discards its String result and keeps the
opaque stack prefix. A distinct next-line sentinel and the exact native
call count detect omitted or repeated effects. The `tail-cell` control
always uses a Cell, while `tail-switch` captures a formerly owned Number
after native code is already installed: the same site's remaining visits
must decline the tail, run the effect once, and continue canonically.

These fixtures use `jit_tail_entries` to distinguish straight-line suffixes
from loops. At the current 64-visit warmup, 137 effect visits give 74 tail
entries; a 63-visit run is cold and the switch fixture gives 18 entries
before the Cell transition. Native tails have no fuel yields. Opcode,
detailed instruction/edge and native-call profiles must agree with the
interpreter control.

To time fresh processes on the same binary and inputs, turn telemetry off
and alternate the automatic tier and `MSYNC_JIT=off` in AB/BA pairs:

```sh
python3 tests/Performance/NativeJit/measure.py .temp/current/msync \
  --cpu 12 --work .temp/native-jit-recurrence
python3 tests/Performance/NativeJit/measure.py .temp/current/msync \
  --fixture nested --cpu 12 --work .temp/native-jit-nested
python3 tests/Performance/NativeJit/measure.py .temp/current/msync \
  --fixture merge --cpu 12 --work .temp/native-jit-merge
```

All samples are checked against the independent oracle after timing. Source
timing includes compilation, object timing includes preparation, and bound
timing uses an already prepared image. All include process startup.
Report the 3-round no-promotion control separately from the hot measurements.

## Guarded direct source calls

Use the existing live-policy corpus generator rather than an inline
microbenchmark. The small seed still warms three real dynamically selected
closures, including a writer and a reader of captured state:

```sh
python3 tests/Performance/Corpus/run.py --baseline .temp/current/msync \
  --case live-policy-dispatch --scale small --seed 17 --route bound \
  --profile off --pairs 1 --warmups 0 --work .temp/direct-source-fixtures
python3 tests/Performance/NativeJit/direct_source.py .temp/current/msync \
  --fixture .temp/direct-source-fixtures/live-policy-dispatch/small/17 \
  --work .temp/direct-source-cli
```

The journey checks the independent output oracle for source, isolated
format-5 object and bound artifact, plus a Linux executable shebang.
Aggregate profiles must retain canonical opcodes, native-operation totals
and logical activations; three actual policy targets must warm before the
direct tier takes over. The separate instruction profile must stay
canonical, including exact PC and edge counts. `--expect fallback` selects
the semantic-only architecture mode when native code is unavailable.

## Guarded Scope AOT region

Generate the independently checked mutable-band fixture and verify its
source, isolated object, and bound paths:

```sh
python3 tests/Performance/Corpus/run.py --baseline .temp/current/msync \
  --case band-classification --scale smoke --seed 17 --route bound \
  --profile off --pairs 1 --warmups 0 --work .temp/scope-band-fixture
python3 tests/Performance/NativeJit/guarded_scope.py .temp/current/msync \
  --fixture .temp/scope-band-fixture/band-classification/smoke/17 \
  --attempts 1024 --work .temp/scope-band-cli
```

The guarded worker is precompiled, so both JIT settings must preserve
hits and logical opcodes. Detailed instruction and edge telemetry must
stay canonical. The oracle observes real changes to a shared Scope's
bounds, not merely a fixed projection of a literal.

For the larger crossing-arm route, generate `segment-screening` with
the same smoke scale and seed 17 instead. Its 96 samples × 8 segments
require `--attempts 768 --crossings 633`. The independent rational
oracle checks live endpoint updates and horizontal/reversed segments.
Aggregate mode records the selected AOT crossings; detailed mode still
checks the canonical PC/edge stream. The JIT setting does not select
this AOT worker.

## Readonly literal Scope fold

Generate `literal-segment-parity` at smoke scale, seed 17, using the
corpus command above. Run:

```sh
python3 tests/Performance/NativeJit/readonly_scope_fold.py .temp/current/msync \
  --fixture .temp/scope-band-fixture/literal-segment-parity/smoke/17 \
  --rows 19 --work .temp/scope-fold-cli
```

The checked paths are actual source and isolated portable/bound artifacts.
The same source receives a separate effectful literal factory; it must
produce one output per demanded row position. Aggregate and detailed
observation preserve canonical factory counts. The optional debugger
replay in the production receipt measures physical pulls separately from
those logical counts.

## Native ordered Number fold

Generate the `ordered-calibration` smoke fixture (seed 17) with the
corpus command, then run:

```sh
python3 tests/Performance/NativeJit/ordered_scope_fold.py .temp/current/msync \
  --fixture .temp/scope-band-fixture/ordered-calibration/smoke/17 \
  --work .temp/ordered-fold-cli
```

The 32 queries over 129 literal rows exercise the 512-row promotion
budget: 29 native chunks in auto and zero off, with 4,128 folded
logical rows and matching opcodes. The journey also covers an actual
Linux shebang and original detailed observation. Use
`--expect fallback` on a host without guest-native publication.

## Whole-method native execution

Use the current Native AOT CLI to produce portable and bound score-checking
artifacts. The same journey can use a separate producer with the same current
schema; it does not depend on historical library-matrix outputs.

```sh
python3 tests/Performance/NativeJit/whole_method.py .temp/current/msync \
  --work .temp/method-acceptance \
  --case set --case heap --expect set:native --expect heap:native
```

Set/Heap verify independently computed scores, including all Heap dequeue positions.
Separate inputs replace a live Set comparator after warming, mutate an
actual arena link between calls in a linked threshold query, and exercise
a two-argument multi-block numeric method. A separate vector-backed link
control preserves the binary64 sign bit on a returned zero; it is not an
arena-index traversal. Auto/off run as actual source, detached objects,
detached bound and rebound artifacts, and POSIX shebang where
supported. Detailed PC/edge maps must agree; aggregate logical counts
are also compared unless the worker reports unmapped entries explicitly.
Per-function physical activations, boxing, typed worker operations, and
fast native dispatches are recorded separately.

Use `--expect CASE:native` to require method-worker admission, or
`--expect CASE:fallback` to require none. `--guard CASE:COUNTER` requires
an observed dynamic guard exit. `--reduce CASE:calls` and
`--reduce CASE:fast-native:arena.key` assert removal of measured
physical work rather than a change in logical opcode counts. A selector
may name one route (for example, `CASE/bound`). `--case` and
`--route` select focused subsets. A Windows x64 executable runs directly
on Windows or with `--wine-adapter` using the existing local Wine setup;
the adapter does not turn a CoreCLR run into Windows Native AOT evidence.

### Heap writer and authoritative rows

The same driver has focused `heap-live` and `heap-ieee` cases. The former
checks sorted output through an actual Heap alias, every retained
snapshot position and the exact count of observable comparisons after
a live same-order policy replacement. The latter checks the order and
signed-zero representation of actual mixed IEEE keys. The receipt keeps
actual `native_worker_stores` metrics distinct from
replayed original native-operation totals.

For the invalid selected index, compile and detach the fixture before the
real-process comparison:

```sh
python3 tests/Performance/NativeJit/heap_writer_index.py .temp/current/msync \
  --work .temp/heap-index-current --expect-native
```

The test first warms valid Heap writes. It then selects index 99 on a
3-element vector owner and compares the actual error, output prefix, original
source attribution and complete logical profile auto/off. The native
arm must have executed a writer before the selected index guard miss.
