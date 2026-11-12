# Exploratory execution backend tracks

The [joint execution target work](tiered-execution-v086.md) provides a stable
baseline. These tracks deliberately explore larger changes separately from the
production patch. The contract is an autonomous portable .mso, a contextual
.mexe with a canonical fallback, and a process-local execution representation.
Use integrated workloads and opt-in diagnostics to judge the product; these
probes establish whether a larger direction is worth integrating.

## Native versus portable machine-code lowering

Try LLVM ORC against the actual Number loop importer with a minimal
unboxed ABI. A second backend candidate is a portable WebAssembly
emitter plus an embeddable engine such as Wasmtime; the first feasibility
probe uses Node/V8 only as a disposable harness. Production must not launch
Node or depend on the Python/toolchain used for experiments.
[LLVM ORC](https://llvm.org/docs/ORCv2.html) offers a native compilation/linking
boundary; [Wasmtime's C API](https://docs.wasmtime.dev/c-api/) offers an
embeddable portable-engine boundary. These are implementation candidates,
not dependencies selected for release.

The same non-reassociated binary64 recurrence ran in a 590-byte
wasm32 module (Clang 22, V8 14.6, Linux x86-64 CPU 11). At 1,048,576
iterations all result checks matched `41000036FA3A7F36`. The medians
of eleven warmed samples were 3.50 ms for one invocation and 3.48 ms for
64-iteration slices. The single observed module construction/instantiation
took 1.02 ms, excluding engine/process startup. This is a C-to-Wasm kernel,
not a measured msync importer or a claim about Wasmtime's embedding cost.
It establishes a plausible low-cost *portable target* for reference-free
numeric regions. The [Wasm arithmetic NaN rules](https://webassembly.github.io/spec/core/exec/numerics.html#nan-propagation)
leave some sign/payload results nondeterministic. The finite recurrence
does not qualify a general arithmetic lowering: keep observable signed-zero
and NaN representation tests, or restrict the admitted operation domain.
The engine call ABI, host helpers, code-cache admission,
startup, cancellation, and captured-reference behavior still need separate
integration evidence. Data and code:
`artifacts/benchmarks/execution-backend-exploration-v086/wasm`.

## Shape of the next region

The current Tier 1 only crosses empty operand-stack edges. Heap's retained
sift-down alone accounts for about 64% of its logical opcode visits, including
nonempty stack joins, live Buffer reads/writes and predicate calls. This is
a better target than adding more local opcode pairs. Build explicit block
parameters for locals and live operands. An exit maps to the next canonical
PC, exact live registers, original frame/inline provenance, and already
executed effects. Keep indexed stores and real ownership boundaries visible
in the region. Set's high-frequency node projection offers a related
cross-call target; the residual wrapper and physical traversal are different
from the comparator frame that is already eliminated. The checked census
finds 6,965,521 canonical visits in Set find (function 36) and another
4,076,235 in insert search (function 24). Heap sift-down (function 19)
covers 6,266,097 visits. Its nonempty operand-stack join at PC 74 receives
86,206 observed jumps from PC 65 as well as fallthrough. Coverage assumes
guarded member and local Buffer/snapshot operations; it is not a prediction
of generated-code speed.

Additional candidates in progress: bounded borrowed String/Path views with
captured provenance, workload-shaped object placement, and exact internal
word operations for SHA without changing binary64 Number semantics.

## Physical text views and exact Word32 operations

A scratch Native AOT VM recognizes a retained lexical Path extension-to-String
comparison and uses an owner-backed text slice instead of creating parsed
segments, a PathValue, and a StringValue. The same frozen executable filters
300,000 input paths. Both hosts return the same count (58,385), and seven
fresh-process pairs give 1254.61 -> 787.01 ms (7/7 faster). In separate
40,000-item profiles both report 1,071,185 logical opcode visits; allocations
are 36.45 -> 13.08 MB. The slice parser matched the original on 600,120
generated/edge cases across POSIX and simulated Windows lexical grammars.
Windows host timing was not measured. The next step is to retain such views
across typed lexical operations, not proliferate instruction-pattern checks
or store borrowed spans beyond their owner/suspension boundary.

A separate scratch Word32 prototype makes AND, XOR, right shift and rotate
right directly expressible through typed NativeCall identities. It changes
neither Number's binary64 semantics nor the format-5 wire opcodes. A 1 KiB
SHA-256 guest keeps its rounds, control flow and Buffer access structure,
while replacing scripted nibble arithmetic with exact checked word
operations. Same Native AOT probe, six alternating measured processes with
one warmup each: 395.76 -> 12.21 ms; 7,574,571 -> 231,919 logical opcode
visits; 41.17 -> 0.79 MB managed allocation. All 27,744 word operations
entered the raw-stack fast path. Empty input, abc, the 56-byte padding
boundary and 1 KiB cases matched independent digests. This is not a
hardware SHA intrinsic or a native whole-algorithm substitution. The
production design still needs stable portable operation semantics and
typed-region lowering; this prototype is not installed. Receipts:
`artifacts/benchmarks/execution-backend-exploration-v086/{path,word32}`.

Taken together, these probes give two ways to reduce *guest work before
JIT*: avoid allocating intermediate text/path values, and express
operations that the current bytecode otherwise expands into thousands of
source calls. Native compilation addresses the remaining cost per region.
Evaluate all candidates on a common workload suite with recording disabled
for timings and counters collected separately; component ablations are not
required for ordinary integrated acceptance.

The fresh Word32 CLI measurements include launch and artifact loading:
446.34 -> 39.41 ms median, 7/7 faster. Use this comparison for the command
journey and the preloaded result for kernel/runtime throughput. Existing
named native identities can carry the operation semantics in .mso, but an
older runtime's closed native registry is not presumed to implement a new
identity. Do not reinterpret a previously emitted artifact.

The region experiment additionally executes explicit operand registers and
block parameters in a model imported from the actual retained disassembly.
Against its baseline on 22 synthetic eager heap/input states, 1,548 forced
pre-operation continuations matched, including 1,134 with nonempty operand
stacks and three exits immediately after a Heap store. This tests successor
PC and operand-state recovery without replaying writes; it does not validate
a production managed-root map, dynamic callbacks, or a machine-code tier.
The census, model and summary are under
`artifacts/benchmarks/execution-backend-exploration-v086/regions`.

## Actual guest JIT proof

The optional scratch LLVM 22 ORC backend consumes the real numeric-loop
importer from a Native AOT msync host. It reads the same frozen format-5
`.mexe`, lowers its imported operations to LLVM SSA, and transfers only
scalar scratch values, the continuation PC and loop fuel through the ABI.
No managed heap pointers cross that interface. This is machine-code
generation from the guest artifact, not a hand-compiled C recurrence.

Nine AB/BA fresh-process pairs pinned to Linux x86-64 CPU 12 measured warm
medians of 50.68 ms for existing Tier 1 and 3.43 ms for ORC, versus 3.06 ms
for the C# reference loop. The same host and guest code run both versions;
all checked recurrence values matched exact result bits. First guest
execution in an already-launched process, including loading LLVM and
compiling the region, was 49.54 ms / 43.84 ms at the median with visible
variation. The library load and first compilation medians were 21.48
and 12.71 ms. Eager JIT is therefore not the default startup strategy.

Cross-emitting the same SSA kernel for AArch64 and executing under QEMU
returned the exact expected binary64 bits, original continuation PC, and
63 fuel yields. This proves cross-target guest code execution, not an
AArch64 Native AOT host acceptance run. A long x86 guest was interrupted
after an arranged 50 ms cancellation, observed at 62.4 ms; this is a
demonstration of the bounded-fuel handshake, not a general latency bound.

## Integration order

Carry the portable Word32 operation contract and borrowed text/path
representations into the bound target IR as parallel product slices. Keep
ordinary Number and Path behaviors as explicit boundary cases, not implicit
properties of host types. In parallel, use the continuation model to compile
a whole read-only Set traversal region (node projection plus comparator),
then a Heap region with a store and a nonempty stack exit. That sequence
tests why the state-map architecture matters, without requiring a full
managed-code JIT before the first useful deployment. Native JIT and Wasm
should consume that same region representation; promote only when useful
execution can amortize the compile/engine cost.

The ORC prototype still uses the ordinary numeric executor under detailed
profiling. Production acceptance needs compilation/entry/exit telemetry and
an explicit bounded code lifetime, including resources still executing.
The local LLVM shared library is about 150 MB uncompressed; the prototype
is evidence for code generation, not a proposal to add this dependency to
every CLI installation.

## Broader acceptance before extending the runtime

Use the [general-purpose workload corpus](../../tests/Performance/Corpus/README.md)
to qualify the next joint backend changes before treating the exploratory
numbers as release evidence. Word32 and the scalar recurrence remain useful
mechanism-specific diagnostics. The corpus exercises real CLI/source and bound
routes and excludes bit operations in guest code; report independent families,
scale, seeds and unchanged/non-hit paths, rather than weighting the score
toward the new operation.

## Subsequent production and discovery work

Word32 is now available through checked named math operations in the
product, without changing Number or the artifact encoding. See the
[production acceptance receipt](../../artifacts/benchmarks/word32-native-v086/README.md).
The Path track likewise moved lexical parsing and selected comparison to
borrowed spans in the product; it is not a change to endpoint provenance.

The [next-frontier experiments](../../artifacts/benchmarks/execution-frontiers-v086/README.md)
identified a second source of the geometry cost: 196,608 fresh Scope rows
from capture-free literal producers. Sharing immutable field storage with
write-side copy-on-write reduced 709.68 -> 461.87 ms and 35.23 -> 13.22 MB
on the frozen polygon artifact, while retaining a fresh mutable identity on
every demand. By contrast, raw numeric payloads in generic captured Cells
removed allocation but did not make the signal workflow faster and hurt
the geometry control. Do not broaden generic load/store for that prototype;
test a typed environment or capture/frame borrowing instead. The literal
Scope path now uses the production immutable template; generic raw
captured Cells are still rejected as a default layout. The production
frame work borrows actual caller arguments instead.

### Consumer-shaped numeric text conversion

A separate Native AOT prototype bypassed the generic native argument array
for ready String/Path-to-Number conversion and kept the canonical parser
and error span. Returning a raw Number unconditionally was a poor fit for
short Range callbacks: they allocated a numeric operand sidecar, then boxed
the result immediately at Return. Keeping the ordinary boxed result for
ordinary Value consumers gave paired median changes of -9.89% for
live-policy and -16.89% for bounded-sensor. Keeping admission on the slow
route also avoided changing the hot `math.floor` switch layout on the
signal control. Prefer a bound proven Number-conversion target and select
raw output only where the consumer actually keeps it raw. The production
fast path now selects bound ready casts and keeps the boxed result. It
consults the target only at the relevant native operation; it does not add
per-frame cast state to unrelated bytecode. See the
[current measurements](../../artifacts/benchmarks/frame-completion-v086/README.md).

Three additional signal controls did not justify independent peepholes:
removing iterator dispatch through source substitution increased boxing;
sharing scalar expressions through extra stack locals traded arithmetic
for slot traffic; pruning a known discarded member read removed 589,824
logical operations but did not improve paired wall time. Preserve
representation across the producer/call consumer boundary instead.
See the [frame-frontier receipt](../../artifacts/benchmarks/frame-frontiers-v086/README.md).

### Raw results need typed consumers

In seven actual corpus artifacts, 118,638 of 138,643 exact Number source
results (85.6%) immediately enter a boxed `StoreLocal`; Signal and live
policy both do so at every such call site. A disposable caller-owned
return window avoided 18,708 and 73,930 boxes in two versions of a policy
control that consume the result immediately in arithmetic, but had no
hits in the original policy, Signal or Heap and no robust stress timing
gain. Do not widen the generic result ABI for this. First let selected
private FrameSlots keep numeric writes and JIT exit commits in an owned
numeric lane, materializing only at actual Value/storage boundaries.
Capture identity, borrowed argument roots and observable snapshots stay
canonical. The [typed-return receipt](../../artifacts/benchmarks/typed-return-frontier-v086/README.md)
contains the paired results, actual site census and proposed slot contract.

### Guarded whole source activations

The [direct-source experiment](../../artifacts/benchmarks/typed-direct-call-frontier-v086/README.md)
compiles already-selected Number closures from bound explicit operands,
including a nonempty return merge and one mutable capture. No managed
reference enters native code. The actual capture owns any commit, and a
guard miss enters the canonical call with the original operands. On the
standard live-policy workload, static per-image call-site admission keeps
27,563 of 27,752 calls per seed native and improves paired CPU time by
10.59% and 10.64%. A broad arity check penalized zero-hit workloads; use
physical admission sites and select raw results only where the caller
keeps them. This is a whole-function entry/return proof, not yet general
mid-region deoptimization or production activation.

### Guarded mixed regions need more work per native entry

The [Scope projection experiment](../../artifacts/benchmarks/guarded-scope-regions-v086/README.md)
joins seven actual corpus artifacts with exact per-PC telemetry. Polygon
function 0 PCs 120–132 execute 196,608 times per standard seed: two live
numeric Scope fields, two local stores and comparison parity. This region is
outside the existing arithmetic tails and could avoid 1,966,080 additional
dispatches after accounting for the local/member fusion. The same narrow
pattern has no sites in the six other measured workloads; most hot Heap,
B+ and Signal members are Buffers or callable policies, not scalar fields.

The scratch bound region checks the actual Scope layout and current
numeric values, keeps ordinary fallback before any state change and
commits to the original private slots. In one Native AOT executable, the
guarded AOT worker improves paired fresh-process CPU by 21.1% and 25.2% for
the two Polygon seeds. The same guards with a dynamically emitted x64
worker also improve on ordinary dispatch, but are 8.5% and 7.8% slower than
the AOT worker. The comparison measures the whole entry and continuation,
not isolated machine instruction latency; the native entry was verified
directly. A zero-site Heap control shows no consistent change.

Prefer an AOT worker for such short mixed regions. Reuse bound region
operands and explicit exit liveness rather than shipping a name-specific
peephole. In Polygon both Y locals are dead on the 162,056 noncrossing
edges in seed 17, but live on the crossing edge. A proved region can defer
their commit to that edge and only then select the later X fields. This
is a next experiment, not a measured additional speedup. A larger region
or a certified loop/batch must amortize the guest-native ABI before using
the generated worker.

The [complete crossing-arm follow-up](../../artifacts/benchmarks/scope-crossing-arm-v086/README.md)
kept the AOT Y prefix unchanged. It imported the actual Bool merge at
PC 169, omitted four dead private writes on the crossing edge, and guarded
the two X fields without inspecting them on the unselected edge. One whole
AOT worker replaces two existing native tails and reduces paired Polygon
CPU by 11.37% and 14.03% on the two standard seeds. The generated whole
worker shows no repeatable advantage over it, including 138,240 real
native entries on the 4× input. Keep the larger guarded region, not a
guest-native transition for this particular grain of work.

### Certified readonly row batches

The [whole-row-loop scratch probe](../../artifacts/benchmarks/readonly-row-batch-v086/README.md)
uses the actual Polygon inner range: 12 closed literal Scope producers,
196,608 selected rows and 7.74 million canonical inner-loop PC visits in
the standard run. Only the parity Bool is live at the exit. The baseline
already has guarded Scope branches and literal factories; the probe
checks the exact normalized function and the actual enumerable's
literal-plan witness before avoiding intermediate row identity and
iterator traffic. A producer that performs a real stdout effect retains
all original demand effects and never takes the batch.

On one Linux x64 Native AOT binary the AOT fold lowers paired CPU by
73.4–73.7% on both standard seeds and 81.0% on the stress input. The emitted
12-row native worker enters once per query, but does not reliably beat the
AOT fold. This is a producer/consumer batching result, not a speedup from
generating machine code or a case for shipping a function fingerprint.
[MonetDB/X100](https://ir.cwi.nl/pub/16497) similarly amortizes tuple-at-a-time
operator overhead, while [Coutts, Leshchinskiy and Stewart](https://donsbot.com/papers/)
use stream fusion to eliminate intermediate values. Here readonly
Scope identity, selected demand effects and IEEE arithmetic must be
proved for the bound consumer; a nominal Range type is insufficient.
