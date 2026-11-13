# Guest execution tiers and backend boundary

The [2026-11-12 storage/lowering correction](object-spatial-layout.md#authoritative-model-2026-11-12)
is authoritative. The tiers below record implemented experiments, not a native
support policy: complete builtin storage and namespace lowering are required
in the canonical representation, not only on selected profitable paths. Further
Number/Scope-pattern workers and collection-specific schemas are not substitutes
for that foundation. Backend profitability may choose execution strategies;
it must not determine which types or source constructs can be lowered natively.

## Representation and ownership

Portable code is an interchange format, not the execution engine's internal
instruction set. RyuJIT imports stack IL into its own IR; HotSpot uses
rewritten interpreter forms as well as independently compiled code. The
[stack-versus-register interpreter study](https://doi.org/10.1145/1328195.1328197)
identifies the relevant trade-off: explicit operands reduce dynamic dispatch
and operand traffic, but can increase code footprint. Do not copy its measured
speedup or couple msync's wire opcodes to host instruction selection.

```text
source -> typed HIR (.mhir) -> portable optimization -> typed bytecode (.mso)
                .mso + binding context -> bound code + I/O recipes (.mexe)
                loaded executable image -> executable targets (process-local)
                                            |-- baseline bytecode
                                            '-- specialized execution versions
```

- HIR owns types, lexical identities, captures, effects and evaluation order.
  Portable optimization owns proofs independent of execution context. It should
  make reusable proofs visible to the backend instead of requiring the backend
  to reconstruct language semantics.
- Executable preparation owns contextual operands, resource plans and residual
  code. Format 6 stays ISA-neutral, without making a bound artifact independent
  of its original context: no host entry addresses, generated native code,
  profile counters, managed references or persistent validity assumptions about
  observed resources. A portable optimization recipe may eventually be
  serialized if it demonstrably pays for itself; this is not required to have
  a specialized target.
- The runtime backend selects or builds execution versions from the bound
  image. Code/cache identity is the projected image and function/site, not a
  bare numeric function ID. Captured values, closures and mutable Scope
  payloads are never shared between invocations through a code cache. Immutable
  version data does not own a live frame. Old frames keep their code generation;
  a new projection does not reuse old variants or replan existing I/O.
- Tier 0 is both the fast startup path and the semantic continuation. Tier 1
  uses explicit typed virtual registers and precompiled host handlers where
  profitable. Tier 2 may compile a profitable region to machine code from the
  same typed representation; the Native AOT product cannot obtain guest
  tiering by enabling the host CLR JIT. Neither CPU-specific instructions nor
  executable memory are needed for Tier 1.

Upstream mechanisms: [RyuJIT importer and IR](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/jit/ryujit-overview.md),
[HotSpot bytecode rewriting](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/interpreter/rewriter.cpp),
[CoreCLR on-stack replacement](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/features/OnStackReplacement.md).
The [earlier managed runtime review](managed-runtime-optimization-v080.md)
records other mechanisms and the limitations of same-block peephole fusion.

## First production slice: private numeric loops

The runtime backend imports a bounded, contiguous region at a repeatedly
taken backward jump. It maps stack operands to explicit Number registers,
folds a compare and branch together, and directs the last arithmetic
operation straight to its destination local where possible. The importer
requires empty stacks at control-flow edges and rejects calls, member access,
iterators, effects and other unsupported operations. A region can leave via
an original PC, with a stack already empty. This is a restricted form of
on-stack replacement: it does not replace a method frame or perform general
speculative deoptimization.

Entry checks actual touched locals once. Only an owned activation with
immediate Number values qualifies; a Cell or deferred value
takes the original path without changing guest state. The backend uses
binary64 scratch registers across iterations, commits changed locals on
normal exit or cancellation, and uses shared arithmetic/comparison semantics.
It does not reassociate operations or introduce fused multiply-add. The
importer does not know local slot numbers in advance and works with existing
format-5 artifacts. The bounded import threshold and scratch limits choose
an implementation, not a language validity rule.

A weak, per-interpreter image cache holds lazy function/site plans. Eight
taken backedges make an eligible site worth one import attempt; a rejected
shape is cached. The constant is a local policy for this first tier, not
a universal promotion threshold. A future profile should separate import,
entry hits/misses, residency, and execution costs before adapting it.

Logical telemetry keeps the original PC/opcode path and edges, including the
exit from a region. `vm.function` exposes `numeric_loop_entries` and
`numeric_loop_operations` separately from canonical `instructions` and
`number_boxes`; the latter includes materialization on region exit. Logical
site replay is restricted to the observed executor, not the telemetry-off
hot path.

## Evolution

1. Make portable proofs of numeric/private storage and known concrete calls
   first-class backend inputs. A Number type alone does not prove an immediate
   representation or an unaliased location. Retain inference in HIR and
   CFG/def-use/effect work in the optimizer. The runtime importer remains the compatibility path
   for old artifacts without stronger proof metadata.
2. Expand the typed execution representation across function and safe memory
   boundaries, starting with actual hot numeric Buffer/comparator work.
   Represent eager numeric values, managed references and Cell locations
   separately. Keep effects and suspension explicit; do not turn the typed
   executor into another scheduler. Measure fresh CLI and prepared VM
   separately against the same artifact and verified result.
3. Add call-entry and backedge profiles, version selection and guarded
   specialization for sites that remain dynamic. Key cached resolution by
   actual shape or callable identity while loading the current payload. Only
   introduce live-state maps beyond empty-stack transitions where concrete
   cases demand it.
4. Compare precompiled typed handlers with an optional native region backend
   on long-running hot residual work. Define continuation, exception/PC,
   managed-root, cancellation and host ABI boundaries before emitting native
   code. Use the same target-independent operation/representation contract;
   architecture-specific performance work does not change .mso/.mexe meaning.

## Validation

`dotnet run --project tests/NumberSemantics -c Release` compiles source into
serialized HIR, lowers and optimizes it, then runs round-tripped portable and
bound artifacts. It checks cold/warm transitions, exact recurrence results,
and compares logical instruction/edge counts and signed-zero results against
a Cell-backed fallback for the same numeric loop.

On Linux x86-64, eight alternating pairs with Native AOT and identical
format-5 scalar code gave a median of 597.97 -> 38.86 ms and 50,333,960 ->
4,896 allocated bytes. Exact result bits and all 15,728,660 logical opcode
visits were unchanged. The tier executed 5,242,842 explicit operations and
reduced the Number materialization counter from 2,097,152 to 18. A real
SIGINT during the promoted loop returned 130 and preserved cancelled
execution telemetry. An existing 4 KiB copy executable still copied all bytes
and its bind output remained identical. The Set, Heap, Hash and SHA-256
benchmarks did not enter this tier; their measured results remained correct.
Raw samples and hashes are in `artifacts/benchmarks/tiered-execution-v086`.

Follow-up fresh CLI controls on the same frozen Hash and SHA-256 artifacts
had 12 A/B pairs with median paired changes of +3.28% and +2.80%, alongside
12 interleaved A/A pairs with -3.09% and +1.24% respectively. These timings
include process and host code-generation effects; they do not isolate the
cost of the backedge hook. Treat non-hit cost as an open measurement, not
a speedup or a proven zero-overhead path. Backend eligibility metadata
should make noncandidate backedges bypass tier selection.

## Backend IR for calls, text, paths, and storage

### Method compilation is the main path, not a collection of loop motifs

The [frozen library replay and diagnosis](../../artifacts/benchmarks/library-matrix-v086/latest-cli/README.md)
provide a concrete counterexample to coverage measured in supported numeric
operations. The 8,192-key Set takes 943 ms as a bound image, with zero native
entries despite a warm traversal and 478,589 precompiled comparator hits.
It still enters 309,586 node-wrapper frames. Every observed Buffer read and
singleton Range first access already hits its respective fast path. An
additional scalar pattern would not remove the repeated composition of these
operations. The 807 ms source compilation span is a separate problem, 736 ms
of which belongs to semantic analysis and HIR construction.

There is also work to remove before code generation. The
[discarded-callee experiment](../../artifacts/benchmarks/backend-discarded-callee-v086/README.md)
identifies 2.69 million Set and 1.61 million Heap visits to member
projections whose callable is discarded before an already encoded
`NativeCall`. The exact same CLI ran the checked diagnostic artifacts in
921 → 824 ms and 394 → 348 ms, respectively. The experiment omits the
projection without a general legality proof. A real lowering should
retain the callee expression's actual effects and possible failures,
not compute a callable that will not be used. This is a compiler
obligation, not a reason to add a dedicated VM opcode.

**Decision:** the unit of optimization is a specialized guest method and
its control-flow graph, with inlining and region continuations. A method
must not match a source name, a fixture CFG, or a single closed list of
arithmetic opcodes. Operations with real host obligations can be outlined
to an explicit continuation; one such operation should not disqualify
unrelated blocks. Existing scalar workers remain useful lowerings, not
independent architectural frontiers to extend one motif at a time.

### Native-first model and validation ownership

The executable graph is the common representation for primitive arithmetic and
addressable storage, not a list of guest opcodes that each tier rediscovers.
An operation retains its canonical PC, explicit inputs/results and continuation
state. Bound numeric calls carry a `NumericIntrinsic` plus the selected
`NumericRepresentation`. The dispatch view encodes that operation once; it
does not re-dispatch on the native's spelling in the hot path. The worker
compiler uses the same graph, typed registers, operation maps and native ABI
for storage-free scalar methods and indexed methods.

Binary64 and Word32 are separate machine representations of guest Number.
An external Word32 operand enters through an exact integer admission; a count
has the additional 0..31 domain. A proven Word32 result stays in a 32-bit
register through later operations. Conversion to binary64 belongs where an
ordinary Number consumer requires it; the original binary64 view remains
separate if it can observe negative zero. For a selected pure method, failed
admission resumes the original method before any operation has run, so the
canonical error retains its original PC. These are not new source types.

A fixed Scope place requires the receiving layout and field ordinal, not a
positive hint. In the current bound implementation, all reaching receivers
must prove one offset; aliases may change field values without changing
layout, whereas reassignable captured locations can change the receiver.
The bound load/store therefore has an offset and no member-name check.
Separately, a capture-free lexical callee becomes a code symbol; a named
declaration whose bindings and identity are unobserved and whose initializers
are inert disappears during HIR lowering. This retains named initialization
when it can run effects or publish a shared instance. For the typed portable
layout cutover, see [the Scope schema](object-spatial-layout.md#portable-schema).

Keep the three representations. Their separation is what allows each
producer to make a different class of fact authoritative:

| Representation | Responsibility | What the next layer must not redo |
| --- | --- | --- |
| Typed HIR (`.mhir`) | Resolve source semantics: lexical identity, type and shape, specialization, evaluation order, and effects. | Analyze source again or recover its semantics from syntax. |
| Relocatable typed code (`.mso`) | Express machine-independent operations and portable optimization, carrying any remaining facts needed by binding and execution. | Reopen HIR, re-specialize, or repeat portable optimization at binding. |
| Contextual executable (`.mexe`) | Bind contextual operands and carry selected resource plans and residual executable code. | Replan recorded work or rediscover portable proofs in the hot executor. |

An optimizing backend may form a process-local graph and machine code
from the executable. This is not another serialized semantic layer.
If the consumer needs a static fact that its input cannot express, fix
the producer/consumer representation boundary rather than merging the
three layers or compensating with dynamic checks.

The current handoff reuses the resolved alternative table for a portable
call rather than parsing the format-5 string in each optimizer analysis,
artifact validation, and interpreter. Producers and function-ID remapping
publish the typed map with its wire text; older wire input decodes once
for that operand. A reader checks each alternative's target, captures,
and arity once; the abstract stack validator uses that result instead of
rechecking the selected alternative. The runtime still selects the actual
callable, which is a value rather than a property of the instruction.
Similarly, a bound readonly fold's private-destination proof excludes
borrowed frames by construction, so neither fold has to check that mode
on entry. It still observes real Cell aliases and producer readiness.

Do not design the new backend as a translation of the existing VM's
physical stack or object protocol. Its input is a typed method graph:
SSA values for ordinary computation, explicit locations for state whose
address is observable, and effects only at operations that actually
have them. Ordinary private slots have a frame layout just as they do
in generated assembly. Their offsets are established when laying out
the frame; loads and spills are not repeated validation sites. A Number
argument, comparison and result use the same scalar representation across
inlined methods. An address-taken value, a nullable value and a deferred
computation are different facts; none justifies giving every scalar a
universal slot, a Cell, or a continuation.

A closed Scope shape can be a typed row with direct offsets, not a
dictionary behind a shape check at each field. A collection owns its
storage and can use stable integer indices across internal relocation.
For a closed Set method the hot model is a root index, a sequence of
typed node records (key and links), and a selected ordering operation.
Neither a Scope per node nor a Range per key follows from red-black tree
semantics. A program that actually exposes those aliases must still see
the established behavior. Keep the representation boundary at the escape,
or retain compatibility storage for that instance; do not require every
instance to pay for a projection that nobody asks for. Do not choose an
alternate storage layout solely because its type is named Set.

The [native-first Set discriminator](../../artifacts/benchmarks/native-first-v086/README.md)
uses the actual lookup and insert control flow with explicit ownership,
layout, and callable facts. The standalone worker measured 4.05 ms
versus 910 ms for the existing bound CLI on the same 8,192 insert /
16,384 query trace, with all 8,192 node records matching. Allocation
and red-black repair are hand-lowered; this measures a possible
representation, not a delivered product backend. The
[separate method-lowering probe](../../artifacts/benchmarks/method-backend-v086/README.md)
also demonstrates why wrapping the old operations in managed callbacks
is the wrong interface: it would cross the host boundary about 156
times per Set lookup and 181 times per Heap sift.

The producer proves ordinary control-flow and representation facts;
the reader checks external artifact structure once; version admission
checks only facts which cannot be known before the actual invocation.
Thereafter, a load at a known frame offset is a load. A loop with no
intervening write or opaque call can keep its selected storage and policy;
if an effect can change one, end the proof's lifetime at that effect
rather than recheck everything after every instruction. Keep observable
index errors, nullable branches, aliases and selected failures. Legacy
artifact validation and the legacy interpreter are compatibility paths,
not templates for the new code generator.

| Fact | Owner | Consequence for execution |
| --- | --- | --- |
| Closed specialization, operand contract, immutable code and known target | Compiler or verified portable bytecode | Select typed operations and inline the target; do not rediscover this at each call. |
| Local is immediate, address taken, escaping, or used through a deferred value | Def-use, capture and escape facts, or a guard on the actual representation | Keep eager scalars and references directly; only use a Cell, box, or deferred protocol where the operation needs it. |
| Live Scope member, policy target, concrete carrier or storage layout | Actual runtime receiver and effect scope | Load the current value; guard only unresolved facts and keep a canonical continuation for changes. |

Format 6 provides typed entry/capture signatures, logical field projections,
static slots, specialized call operands, and explicit location operations.
It does not serialize the HIR type of every local and intermediate. A
specialized target does not assert that a mutable member still contains a
particular closure. Reuse the bound explicit-value graph and derive remaining
execution facts once per hot method. Convey a proof discarded by lowering in
the portable representation when needed, rather than loading HIR or source
at bind time. Direct source and standalone format-6 artifacts use the same
backend. Keep the cold interpreter path without constructing optimizing
graphs for all library functions at startup.

For Set, the first end-to-end target is the full lookup body, including
the indexed node read, typed fields, ready payload projection and comparator.
Then use the same vocabulary for mutating Heap sift and for a non-container
String or Path method. A hot per-op native-to-managed callback is still a
dispatch loop: test the whole traversal, not only whether it emitted code.
Managed references remain rooted by the host. A native region may borrow
pointer-free storage only under the actual ownership and lifetime contract;
otherwise lower the access through an appropriate managed operation or
outline at the semantic boundary. No new GC or generic object format is
required to start. A post-store exit resumes **after** that store; a
policy miss resumes with the actual selected operand and remaining work.
The same original-PC and state-map contract serves managed and native
versions, including Windows.

Treat the Set carrier as a separate representation question. NIL, absent
result, a present Null key, comparator replacement and externally visible
Scope aliases are semantics where exposed. A singleton Range allocation
is not automatically a semantic requirement for an internal key read.
Removing it from the common representation requires proving the boundary
or preserving the established visible projection, not changing what
`null` means or adding a Set opcode. Measure the storage intervention with
the tree algorithm and comparator held fixed, then combine profitable
changes with method compilation.

Acceptance is lower absolute wall and CPU time on the **unchanged** Set
and Heap fixtures plus a distinct workload, with source and standalone
artifact routes. Record native coverage as the hot methods and actually
removed dispatches/frames, not just the count of generated functions;
record cold preparation, code size, and the live-policy/committed-store
continuations separately. Do not interpret replayed logical opcode
counters as physical dispatch. This follows the useful part of
[Maglev's representation selection and frame-state approach](https://v8.dev/blog/maglev)
and [Truffle's partial evaluation of stable guest facts](https://chrisseaton.com/truffleruby/pldi17-truffle/pldi17-truffle.pdf):
compile what is known and localize the uncertain part, rather than
requiring the whole method to avoid uncertainty.

Use one typed **execution IR** for precompiled handlers and future native
code generation. It represents explicit value operands, block edges, storage
locations, effects and continuations; it is not another source type checker
or a separate middle end. Compiler-owned facts have provenance through the
shared CFG and def-use graph. The importer of old format-5 code may rederive
what it can prove and mark the rest unknown. Do not encode every fused
instruction in the wire format. If executable preparation needs to persist
recipes later, make them ISA-neutral and validate them as optimizations, not
new assumptions about guest values or a selected external resource.

A value's language type, runtime representation and location identity are
independent. For example, a Number can be immediate binary64, nullable,
deferred, or stored through a Cell. A closed generic instance does not make
those cases equivalent. The backend can carry narrow, independently checked
facts instead of assigning every value a universal object slot.

| Value or operation | Candidate representation | Boundary that still matters |
| --- | --- | --- |
| Number / Bool / Null | Unboxed scalar, explicit nullable/union state only where needed; exact integral index as a proved internal representation | IEEE grouping, NaN/signed zero, numeric range, Cell and deferred ownership |
| String | Keep the managed string reference directly in typed operations; use nonescaping owner/offset/length views to avoid a copy between text operations | Unicode scalar order/index semantics, escape and suspension; do not replace scalar order with UTF-16 unit order |
| Path | Keep raw spelling and captured origin/endpoint/projection distinct; share immutable provenance for related paths and avoid intermediate wrappers in lexical chains | No reanchoring at the current cwd, separator normalization across endpoints, or caching filesystem observations as lexical facts |
| Scope and Buffer | Direct guarded slots for the actual shape; row-oriented for traversals, column-oriented for homogeneous scans when measured; stable logical object and slot identities | Preserve aliases, Cell promotion, snapshot membership, layout changes and GC-visible ownership |

Treat contiguous node data, reference-wrapper count, object lifetime retention
and locality as separate quantities. Today's Scope already has reference-free
numeric pages with row-owned managed payloads; do not add a second page pool
or a wider header to every Scope. For Set, the hot path reads an indexed row,
projects its one-element payload range and follows a link. Test fewer layers
on this path before proposing a global Scope layout. Only scalarize a snapshot
or row when identity and escape analysis justify it. A managed reference must
remain visible to the host GC even if a region has native code.

### Which calls survive monomorphization?

Monomorphization selects a typed body. It does not prove that a live member
still holds a specific callable, supply the actual captures, or require an
activation. Classify calls separately by actual target, effects/ownership,
and code-size economics:

1. **Proved small target, no new work owner:** inline into execution IR, or
   specialize an operation only if it preserves every argument/callee
   evaluation and possible failure. Buffer reads are not generic I/O; a
   selected read can still fail and deferred operands can still need resolving.
2. **Stable observed target, not proved:** load the live callable once, guard
   its relevant identity and the chosen specialization, and use that callable's
   captures. A miss resumes the original Call before it has consumed operands
   or executed effects. Do not guard only receiver shape, argument type, or
   the positive specialized Call operand. Keep resolution and profile state
   per image/site. See [CoreCLR guarded devirtualization](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/jit/GuardedDevirtualization.md).
3. **Real call boundary:** pass typed arguments/results directly in a
   versioned calling convention when practical. Keep distinct ownership
   where a callee can admit scheduled work, and keep general dispatch for
   actually variable sites. Recursion does not imply a host async wrapper on
   every call; proper tail transitions are a separate optimization.

The existing Set evidence distinguishes 309,586 small node wrapper entries
from 478,589 comparator entries. The wrapper's bound body still evaluates
module/member access, reads `self.nodes` and performs the Buffer get. The
natural comparator is not equivalent to a single generic Number less-than:
the full policy orders Null and puts NaN last, and the actual `order.less`
member is live. Test a same-shape replacement policy, different closure
captures, native callables, deferred inputs and signed-zero/NaN before
accepting guarded inlining.

### Native compilation and deoptimization

A guest JIT is a consumer of this IR, not a reason to change the Native AOT
host. First use a native leaf/region prototype with raw scalar inputs and
outputs, managed operations via explicit host boundaries, and no movable
managed pointers hidden in generated code. Measure compile cost, executable
cache size and warm speed separately before choosing a backend; [LLVM ORC](https://llvm.org/docs/ORCv2.html)
is a concrete prototype option, not yet a product dependency.

Entry guards do not require general deoptimization. Mid-region exits do:
map each exit to the next original PC, live typed locals and operand state,
inline frame origins and owned effects. Reconstruct only required state,
preserve shared Cell/Scope identities, and never replay an already executed
call, store, transfer or iterator action. Start with no speculative
elimination across such effects. Add materialization recipes and stable
virtual-object identities only when eliminating escaping allocations is
profitable; use the [HotSpot deoptimization implementation](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/runtime/deoptimization.cpp)
and [partial escape analysis work](https://ssw.jku.at/Research/Papers/Stadler14PhD/)
as implementation references. Keep on-stack replacement and deoptimization
on a shared state-map contract, not independent frame models.

Initial discriminators: an unmodified Set workload and a live-policy
replacement (frames, guards, fallback and failure order); a String workload
with shared prefixes and supplementary-plane characters; a Path batch
covering relative origins and POSIX/Windows endpoint spelling. Check the
same observable results before comparing warm execution, cold preparation,
allocations, and retained memory. Benchmark layout changes separately from
call elimination so their effects are attributable.

Collect call-target histograms, physical activations and logical inline-call
attribution separately. For the native tier, also record compile time,
version selection, guard hits/misses, exit reasons, materializations and
machine-code bytes. A closed type or frequent site does not by itself repay
compilation; use measured remaining work and expected savings, and keep
ordinary I/O-bound runs on the existing low-startup path.

### Set call ablations (November 11, 2026)

An isolated compiler overlay exempted direct `storage.buffer_get` from the
inliner's Read gate. On the stock 8,192-key Set workload it changed a few
guarded field hints, but left all 1,008,030 interpreter frames, 20,861,818
opcodes and allocation unchanged. In particular, lifting the effect gate
alone does not prove the live Scope-member callee of a hot call.

A separate Native AOT overlay ran the exact same frozen O2 artifact and
bypassed one known numeric comparator body after ordinary callable
selection. Its fixture-specific function ID is not a production design.
The checked Set score was 73728 on every measured run. The physical frame
count fell from 1,008,030 to 529,441 while all 478,589 comparator-target
visits remained. Profiled execute allocation fell from 82,946,288 to
56,136,624 bytes with unchanged Number box and native call counts. On
Linux x86-64 CPU 10, seven AB/BA pairs gave 1315.75 -> 1009.25 ms for the
checked preloaded probe and 1261.48 -> 1043.53 ms for fresh CLI, 7/7 faster
in both. This measures removing the frame and the interpreted body together,
not the cost of a call instruction in isolation.

These results favor guarded call specialization in typed execution IR as
the next Set-facing slice. Keep a static proof path for genuinely known
wrappers and measure call elimination independently of changing node or
Buffer layout. Receipts and reproduction overlays:
`artifacts/benchmarks/execution-ir-calls-v086`.

## Joint slice: predicate targets derived from bound bytecode

**Binding contract.** A standalone `.mso` plus binding context is the
compiler input to `.mexe` preparation. Contextual I/O planning may additionally
observe the selected resources; it never opens the program's source or HIR.
Running or explicitly binding a `.mhir` first lowers it to the same portable
bytecode; this is a different entry point, not an input to `.mso` binding.
If an execution proof must survive independent compilation, encode it in the
portable IR or derive it there. An indispensable out-of-band HIR fact would
break the standalone artifact contract.

The executable backend now derives immutable, per-image predicate targets
from bounded capture-free binary function bodies. It checks exact instruction
semantics across the three text order relations or the six Number relations
(including left, right and both NaN). A version is admitted only when every
supported relation selects a valid effect-free Boolean result. A small
relation mask and original-PC paths replace the physical predicate activation.
Neither source identifiers nor a fixture function ID enter the selection.

The VM still resolves the actual callable and specialization before applying
a target; an eager Number, String or Path representation must match at entry.
A Null, Cell or deferred operand goes through ordinary argument handling and
the canonical body. Text uses the existing Unicode scalar comparator and a
Path's existing lexical projection; it does not rewrite or recapture a path.
The cache is keyed by exact immutable code image, never a numeric function
ID alone, and no plan is serialized. Format 5 is unchanged.

Profile events preserve the selected logical call target and the original
predicate PC/edge path. `vm.function.calls` continues counting actual
activations, consistent with `vm.frame` spans; `predicate_calls` and
`predicate_guard_misses` expose frame-free entries and dynamic fallback.
Joint acceptance compares the integrated VM with the accepted product
baseline, rather than requiring component ablations. Test coverage includes
all IEEE order classes, Null fallback, Unicode and Path projections and two
live callees at one original call site.

The joint backend also marks structurally possible numeric-loop backedges
when preparing execution targets. Known noncandidates skip runtime warmup
and importer lookup; candidate stack/control legality and eager local values
are still checked by the existing importer and entry guard.

On Linux x86-64 CPU 10, seven fresh CLI AB/BA pairs with the same frozen
artifacts measured 1292.58 -> 998.57 ms for Set, 564.33 -> 470.24 ms for
Heap, and 645.52 -> 65.24 ms for the scalar recurrence. Set/Heap eliminated
478,589/191,943 physical predicate frames with unchanged logical
instruction counts. All four Native library results matched their oracles.
A 12-pair quiet repeat on the two non-hit workloads gave median paired
changes of -14.16% for Hash (no attributed gain) and +0.86% for SHA-256;
the latter did not reproduce a clear regression. The isolated CLI binding
journey removed source before binding a standalone .mso and recorded both
String and Path execution targets. See
`artifacts/benchmarks/joint-execution-targets-v086` for raw pairs and receipts.

The [execution backend exploration note](execution-backend-exploration-v086.md)
tracks native and portable code generation, larger typed regions, and physical
representation experiments separately from integrated acceptance.

## Embedded native numeric backend

On Linux x86-64/ARM64 and Windows x86-64, an imported numeric region can
promote after 128 additional taken backedges. The Native AOT host emits
SSE2 or AArch64 machine code directly from the same region. It needs
neither an installed compiler nor LLVM, and promotion works on a direct
source-script invocation just as it does on a portable or bound artifact.
Set `MSYNC_JIT=off` to retain the
numeric interpreter tier; unsupported architectures, incompatible operations
and unavailable executable mappings also retain that tier. Executable pages
are never serialized.

The entry ABI passes scalar scratch, fuel, optional opcode counters and an
internal resume PC. The x86-64 emitter uses only registers volatile on both
[x64 ABIs](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170),
while the [ARM64 emitter](linux-arm64-guest-jit-v086.md) follows the AAPCS64
leaf ABI. Neither calls the host or manipulates its stack. Hot values stay
in volatile floating-point registers; the emitter spills written locals
before leaving native execution.
An ordinary exit returns the complement of the canonical bytecode PC.
A fuel yield returns the internal backedge target; nested loops may resume
at a different header. The host polls cancellation between native runs
and materializes changed locals at the region boundary, including cancellation.
The fuel budget bounds guest backedges rather than wall time. Resume state
contains no managed pointers.

Native comparisons select condition codes that distinguish unordered operands.
Floating-point operators remain scalar binary64, with no contraction or
reassociation; a sign change flips the sign bit. Remainder currently stays
on the numeric interpreter, as do effects, captures and deferred operands
that the numeric region proof cannot admit. The current loop input and
continuation shape still comes from the existing numeric importer; the
native target itself is not specific to a script or an opcode sequence.

A live loop version owns its writable-then-executable OS mapping through a
safe handle. The executing call retains the mapping until it returns; retiring
the owning image/interpreter allows it to be reclaimed without use-after-free.
The publication steps follow [Linux page protections](https://man7.org/linux/man-pages/man2/mprotect.2.html)
and [Windows protection and instruction-cache publication](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualprotect).
Linux ARM64 additionally clears the instruction cache and synchronizes
other threads' instruction streams before exposing the page.
On telemetry-off execution there are no native counters. Profile mode
compiles a counted version to preserve logical opcode totals and export
`jit_compilations`, `jit_entries`, `jit_yields`, `jit_compile_ns`,
`jit_execute_ns` and `jit_code_bytes` in `vm.function`. The JIT execute
duration includes native dispatch and fuel checks, not interpreter time
before promotion or host-side counter merging. Instruction/site and
diagnostic profiles retain the interpreted numeric path to preserve exact
original edge reporting. These are deliberately different observation modes.

[Native JIT journeys](../../tests/Performance/NativeJit/README.md) exercise
implicit source execution, standalone portable and bound artifacts, real
native promotion, unchanged opcode attribution, numerical corner cases,
nonpromoting cold and unsupported paths, and Linux cancellation.
The [general-purpose corpus](../../tests/Performance/Corpus/README.md)
guards against attributing this native numeric speedup to workloads that
never enter the tier.

The real CLI acceptance includes Linux shebang execution with native hits,
Linux SIGINT, multiple native resume headers, and independently checked
IEEE output. See the [embedded JIT receipt](../../artifacts/benchmarks/native-guest-jit-v086/README.md)
for wall times, exact profile totals, both x64 calling conventions executed
on Linux, and the non-hit corpus repeats. Windows executable publication is
implemented but still needs Windows-host acceptance.

### General bound regions

`Msync.Optimization/Execution/Regions` imports canonical control flow into
explicit ordered values and block parameters from the bound bytecode alone.
It preserves nonempty operand-stack merges, distinguishes iterator yield
from exhaustion, and retains compact persistent stack recipes for each
pre-operation PC. Local slots remain canonical: the projection does not
pretend to prove a representation, an alias, purity or ownership. Effects
remain attached to the canonical instruction; an operation whose dispatch
can absorb the following Pop is marked as a canonical-dispatch boundary.

`Msync.Optimization/Execution/Workers` owns instruction and continuation
recipes, not ISA emitters or activation state. `Runtime/Execution/Tiering/`
groups numeric loops, folds, worker emission/execution, and smaller dispatch
bridges by their execution owner. `Runtime/Platform/NativeCodePage.cs` owns
platform page protection for all native tiers. None of these execution plans
or pages is written to an artifact by `Optimization/Contextual/`.

The numeric tier now consumes this common importer. On the first warm
site in a function it projects the bound CFG once; per-site selection and
register assignment stay lazy. A selected numeric region uses block stack
parameters as the contract for incoming operands, similarly to
[MLIR's block arguments](https://mlir.llvm.org/docs/LangRef/#blocks).
Stack parameters and exit values use stable numbered scratch slots.
Incoming values must be immediate Numbers; a failed guard leaves the
entire original stack untouched. Both the register interpreter and native
backend use the same edge moves, including nonempty branch joins, fuel
backedges, and exits with live operands.

Ordinary local loads need not eagerly copy their Number, but a write that
could change a live old operand snapshots it into scratch before the store.
Copies to edge slots run high-to-low because the original register of a
duplicated operand is no higher than its first stack occurrence. The native
epilogue preserves written locals and cached stack scratch on yields and
exits. A fuel exit's resume PC identifies an exact internal header; a
canonical exit uses its verified stack depth to repopulate the ordinary
operand stack. Synthetic moves carry no bytecode visits. A separate
profiling mode still uses canonical instruction edges.

The shape importer is tested on format-5 round trips including retained
Heap sift-down code; the numeric projection and native execution add
real source with a nonempty branch merge and a portable artifact with
two live values across its loop entry, backedge and exit. This is not a
general mixed-effect deoptimizer. General calls, Buffer operations and
managed reference representations still need an explicit typed boundary
and continuation plan (analogous to the separation of code and live-value
locations in [LLVM stack maps](https://llvm.org/docs/StackMaps.html)).
No source or HIR dependency is introduced.

### Numeric continuation after a canonical effect

The bound image identifies short numerical suffix candidates after ordinary
source calls, native calls, Scope member reads and Scope member writes.
This tier begins **after the original operation has completed**; it does not
perform the effect in machine code or route a failed guard back to that
effect. The owning interpreter performs normal argument evaluation, checks,
suspension, diagnostics and effects. After a successful suffix it resumes
at the first unconsumed canonical instruction; after a rejected suffix it
resumes at the original next instruction.

The same bound CFG, numeric registers and platform emitter lower the suffix.
An untouched operand-stack prefix remains managed and can contain opaque
references. Entry values read or exported by the region must be ready
Numbers. A value only discarded by the canonical suffix (for example, a
member write's Null result) does not need a numerical representation.
Existing NumericLoop ownership rules still require ordinary private slots
for accessed locals; Cell and deferred values remain canonical. A cheap
necessary captured-slot probe rejects a common non-hit workload before
preparing a site. Repeated dynamic misses use a bounded retry backoff,
so a later representation can still enter. Neither probe mutates guest state.

Only a straight-line slice in one CFG block is admitted by this version.
The minimum of three arithmetic operations and repeated site-entry warmup
are tier economics, not language restrictions. Native mappings and operands
stay scalar-only; there is no managed callback from generated code.
The [TraceMonkey side-exit model](https://web.stanford.edu/class/cs343/resources/tracemonkey.pdf)
illustrates the general need to recover enough state at an exact exit;
this narrower boundary avoids needing to replay or rematerialize an effect.

Profile mode reports `jit_tail_entries` and `jit_tail_rejections` with the
original bytecode counts. Exact instruction/edge diagnostics and JIT-off
execution use canonical dispatch for these suffixes. The reported declines
are actual readiness checks, not all visits skipped by adaptive cooldown.

The [combined acceptance](../../artifacts/benchmarks/effect-tail-v086/README.md)
includes a real effect that cannot be replayed (a consumed stdin line),
both immediate and Cell-backed continuations, and mutation after an already
installed native suffix. Standard polygon, Heap and Unicode workloads all
entered this native tier; the signal workload exercised cheap rejection
without native entry. The same final source snapshot passed focused
Windows-branch tests under Wine CoreCLR, not on a Windows Native AOT host.

Split capture/argument frames share the same logical slot view with native
regions. A straight-line effect suffix can read a ready numeric Cell whose
location is not written inside the suffix. All written slots must still be
ordinary Number slots; if a write would affect a Cell, even through a
different alias, the suffix declines. Long-running numeric loops do not
cache Cell payloads. Changed borrowed ordinary locals detach at the canonical
commit boundary, so the caller's original normalized roots are preserved.
See [the frame ownership contract](managed-runtime-optimization-v080.md#split-borrowed-environments-and-live-cells).

Bound ready Number-text targets and borrowed raw arguments cover different
consumer boundaries. An exact nonnullable Number witness and fallthrough
predecessor admit the fast String/Path conversion, but an ordinary Value
consumer still receives its boxed result. An already raw caller argument
can instead stay in the suspended caller's sidecar through the callee's
numeric region or further numeric operations. This does not add a dynamic
representation check to ordinary boxed frames. See the
[borrowed argument ABI](managed-runtime-optimization-v080.md#borrowed-numeric-arguments).

### Bound local member dispatch

The unobserved interpreter can execute an adjacent LoadLocal/GetMember as
one dispatch. The bound image owns a separate dispatch view of producer
instruction arrays with eligible pairs. It replaces the first element with
a synthetic tag carrying the local index and member operands; the original
GetMember remains at the second PC. Neither the format-5 artifact nor the
canonical image changes. The frame selects its dispatch array once, so
canonical LoadLocal instructions do not pay a fusion eligibility check.
Exact instruction profiling and tracing use the canonical array.

The fused path reads the actual local or Cell payload and checks
cancellation at both original positions. It passes the receiver to the
ordinary member handler without an intermediate stack round trip for a
reference. It does not resolve a deferred member eagerly or reinterpret a
layout hint as proof. Failures use the canonical PC's span; after the read,
the existing effect-tail admission sees the original GetMember PC and
canonical bytecode. A jump directly to that PC executes the ordinary
GetMember against the operand already on the stack.

The [member-dispatch receipt](../../artifacts/benchmarks/member-superinstruction-v086/README.md)
joins exact per-PC telemetry with frozen executable disassembly. In the
4,096-ticket Heap and standard B+ journeys, about 70% of member reads occur
in these pairs, yielding approximately 11.5–11.6% fewer dispatches without
dropping logical work. On one Linux x86-64 Native AOT image, toggling only
the dispatch view reduced paired median process CPU by 6.9–9.0% across
the two seeds of each journey. This validates the mechanism independently
of comparing different native links; it does not predict the gain for
other programs. Scratch-only toggle logic is not shipped.
The isolated production slice, with its shared member-tail epilogue,
reduced paired median CPU by 8.6–12.8% on the same four runs. Its Linux
Native CLI retained the selected failure and source span, direct entry at
the second PC, and the bound member-tail result with JIT enabled or off.

This is a static superinstruction, not adaptive quickening or a second
portable optimizer. [Vmgen](https://lampwww.epfl.ch/teaching/archive/advanced_compiler/2007/resources/papers/vmgen.pdf)
illustrates why reducing dispatches can pay off; the two canonical PC
boundaries and the effect-tail ownership are specific to msync.

### Guarded native source calls

The bound image can also choose a whole source activation, not just a
numeric loop or the suffix after an effect. The process-local dispatch
view tags calls whose original direct target or closure-dispatch table
can select an admitted function. The original Call and its PC remain
canonical. Ordinary call sites do not check direct-call admission.
The selected site resolves the actual closure and function once; a
guard failure enters its normal activation with the original operands
still live. Tagged and ordinary calls share the same asynchronous
invocation/continuation and effect-tail code. A miss is not a second
dispatch from an earlier PC.

The first admitted body is a small, acyclic one-Number function with
zero or one numeric capture and an explicit one-value Return. A guard
reads the actual ready argument and capture, not just their static
types. The guest worker receives scalar scratch, no managed references.
It cannot call the host, suspend, or run a source effect. One possible
capture write is staged; the host commits it through the actual Cell
after native completion, while a plain capture rebinding stays private.
The result replaces the callee and argument without touching any stack
prefix; only a consumer that can retain the numeric representation is
offered a raw result. Other consumers get the ordinary Value. An
implicit function exit and unsupported control flow stay canonical.

Per-interpreter target state warms on invocation; the first 63 calls
are canonical and the next can publish the existing platform emitter's
worker. Aggregate profiles report `direct_source_calls` separately from
physical `calls` and preserve canonical opcode totals. Instruction-site
profiles, timelines, session images and `MSYNC_JIT=off` use the ordinary
call path. The [scratch evidence](../../artifacts/benchmarks/typed-direct-call-frontier-v086/README.md)
explains why admission moved out of the generic call handler. This is
entry-only speculation: a future native region that crosses observable
effects needs continuation maps for those effects, not a replay of Call.
See the [joint acceptance and measurements](../../artifacts/benchmarks/direct-source-joint-v086/README.md).

### Short guarded Scope regions in the AOT interpreter

The process-local dispatch view recognizes two hinted member projections
from the same local receiver, stores to distinct private slots, and two
ordered Number comparisons against the same threshold followed by Bool
equality. The plan carries the original field names, slot hints and local
operands; it does not depend on a namespace, a source spelling or a
particular function. Only the entry PC is replaced. The original
instructions, including an incoming edge into the middle, retain their
normal meaning.

The selected worker reads the actual receiver and checks its field
offsets, current numeric payloads and ready threshold. A destination
Cell, borrowed frame, session, unready value or wrong layout declines
before changing the stack or any guest location. The original entry
then runs with its original error and span. The successful region has
no call, suspension, external work or aliased writes. It polls cancellation
at dispatch entry and again before committing the two private stores and
Bool result; it does not hide a cancellation boundary around an effect.
It never enters the guest-native ABI.

If the next canonical operation consumes that Bool with an ordinary
conditional jump, the plan also has two explicit exits. It follows the
reachable bytecode from each successor until a destination is read,
overwritten, or the activation ends. A read includes storage/cell access
and an iterator's local operand. At each exit it commits only values
needed before the next definition. This is valid for the guarded bare
private slots; an actual Cell still declines the whole region. The
handler consumes the Bool without pushing it and leaves any earlier
stack prefix unchanged. The conditional PC remains canonical for
ordinary entry and retains a logical visit in an aggregate profile.

Aggregate profiles count the original opcode sequence and expose
`guarded_scope_regions` and `guarded_scope_misses` separately. Detailed
instruction-site profiles and timelines remain canonical, and the AOT
path does not depend on the guest JIT setting. The exact ScopeLayout
fixtures exercise signed-zero and NaN bits, raw and boxed local stores,
layout/Cell/deferred fallback, lower operand retention, interior entry and
canonical observations. See the [same-image worker comparison](../../artifacts/benchmarks/guarded-scope-regions-v086/README.md)
for why the short region stays in AOT and the
[production acceptance](../../artifacts/benchmarks/guarded-scope-regions-v086/production/README.md)
for source, artifacts, and the independent live-band workload.

### A guarded continuation for the whole crossing arm

A selected Scope comparison can attach a continuation at the actual
crossing successor. The planner imports bound stack values only after
the nearby projection shape qualifies. It verifies the two comparison
branches and their common one-Bool exit, then matches the ordered
arithmetic expression graph. It uses the same local read-before-write
proof as the prefix's branch plan; every suppressed destination is dead
at the common exit and still must be a non-Cell slot at runtime. No
operation names, field spellings, source files or artifact fingerprints
serve as the admission key.

The prefix completes as usual. Only its crossing edge can read the
actual X fields; a layout, payload or Cell miss enters that edge's
canonical PC with the original Y writes complete. The worker neither
replays the prefix nor runs an unselected field access. Successful AOT
execution reproduces the original subtraction/multiplication order and
the IEEE comparison result, pushes exactly one Bool, and continues at the
canonical join. Aggregate `guarded_scope_crossings` and
`guarded_scope_crossing_misses` distinguish this from both the Y region
and `jit_tail_entries`; logical opcode counts include only the original
taken arm.

The [production measurements](../../artifacts/benchmarks/scope-crossing-arm-v086/production/README.md)
use both Polygon and a higher-crossing-density, live-endpoint script.
Larger batching must additionally prove the producer's demand is pure,
row identity does not escape, and the reduction permits regrouping.
The [LLVM loop-vectorizer documentation](https://llvm.org/docs/Vectorizers.html#reductions)
illustrates why a Boolean parity fold does not authorize reassociation
of an arbitrary binary64 reduction.

### Certified readonly row fold

The bound importer can certify a finite iterator loop whose yielded Scope
only contributes ready Number projections to a crossing test and whose
only live write is a private Bool parity. It follows the actual block
arguments and copies of the row, verifies the two comparison arms and
backedge, and checks other written slots at the original exit. Neither a
fixed row count nor a source/bytecode fingerprint is part of admission.

This consumer proof is not a statement about every value of the Range
type. The runtime also requires the actual Items object to carry a
literal-producer witness from the same interpreter and image. Every row
recipe must be capture-free and immutable, with no deferred or partially
proved producer. This is analogous to the exact source-type check in
[LINQ's TryGetSpan](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Linq/src/System/Linq/Enumerable.cs),
not an eager assumption that all enumerables can be replaced by buffers.
Before skipping initialization as well as stores, the runtime checks
all elided destinations for Cells: leaving an old Cell behind could
make a later StoreLocal observable even if no earlier load reads it.

The worker projects numeric values into a weakly owned table keyed by
the actual producer and plan. It creates no per-row Scope or iterator.
Its arithmetic preserves operation order within each row and toggles
only the private parity. A failed proof or live guard leaves the operand
stack untouched and enters the original GetIterator PC; no producer has
been pulled. The batch observes the same demand cancellation per row;
distinct live producer and consumer tokens use canonical linking.
Observed frames retain ordinary iteration and actual factory/PC counts.
See the [production receipt](../../artifacts/benchmarks/readonly-row-batch-v086/production/README.md).

This removes an intermediate row representation, related to
[stream fusion](https://donsbot.com/papers/), rather than making a
stronger floating-point or eager-evaluation assumption.

### Native ordered Number recurrences over literal rows

The same producer witness can feed a different consumer proof: one
private Number accumulator across an exact non-suspending iterator body.
The bound importer records its ordered scalar operations, actual Number
field projections, invariant Number inputs and constants. The operation
graph preserves operand order and permits addition, subtraction,
multiplication, division and negation. Additional loop-carried state,
live written temporaries, Cell destinations, row escape and effects
leave the canonical iterator path. No formula, field spelling or source
file is used as an admission key.

Projection creates a reusable row-major Number table on the actual
literal producer. An admitted fold can use the same typed plan without
native code. After a per-site budget of 512 ready row visits the
interpreter may publish a guest-native worker. The worker receives the
pinned row pointer, count, ready invariant pointer and accumulator;
it returns just the next accumulator. It receives no managed object
and has no host calls. Windows x64 follows the
[Microsoft ABI](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170),
including its fourth-position floating argument, while Linux ARM64 uses
the [AAPCS64 argument banks](https://github.com/ARM-software/abi-aa/blob/main/aapcs64/aapcs64.rst).
Both and SysV x64 use the existing write-then-execute page publisher.

Native calls process at most 256 rows before polling demand cancellation.
The accumulator crosses chunk boundaries unchanged; no reassociation,
parallel reduction or multiply-add contraction is allowed. The first
failed guard enters the original GetIterator before any demand or
write. Native emission pressure or disabled JIT still permits the
ordered AOT evaluation. As in the Bool fold, differing live producer
and consumer tokens keep canonical linked cancellation.

Aggregate telemetry records `number_folds`, `number_fold_rows`,
`number_fold_native_chunks` and guard misses, replaying the original
iterator/body opcodes and reporting logical producers as
`folded_literal_rows` rather than physical factories. Detailed
instruction and timeline lanes stay canonical. This separation allows
the same source-free image and exact opcode oracle to compare the
ordinary, AOT and actual native paths.
See the [production evidence](../../artifacts/benchmarks/ordered-number-fold-v086/production/README.md).

## Native indexed methods and effect continuations

The production indexed method importer consumes the bound control-flow graph
and the existing function type/arity declarations. It derives fixed primitive
rows, private slots and the actual member/callable inputs needed at entry.
No field-name lookup, Value boxing, guest VM frame or host callback remains
inside an admitted native traversal. The existing image format is unchanged.

A legacy Buffer can supply a reference-free compatibility projection of its
actual rows, owned by that Buffer and invalidated by real membership or row
alias writes. No row watcher is attached unless the whole projection
succeeds. Live callback-producing transfers exclude speculative reads while
they can execute concurrently; ordinary unrelated buffers and fields do not
participate in a global epoch. This is not the authoritative storage for
native stores. An original Scope or Range alias remains observable.

A prepared native prefix can reach a real effect and hand back the original
PC with its live locals before selecting the operation. The canonical
dispatcher and its synchronous caller take the same incomplete-await
continuation path as other guest execution. Do not restart a method at
entry after an effect has started. The importer classifies the relevant call
frontier using the same capability as inlining; it does not rerun whole-image
effect inference to discover a boundary. General nonempty successor stacks
and writable scalar/row owners are the continuation of this design.

[Exact Native AOT measurements](../../artifacts/benchmarks/native-worker-integration-v086/README.md)
show the old-format Set and an independent indexed traversal actually
enter machine code. [Scored effect-prefix journeys](../../artifacts/benchmarks/whole-method-journeys-v086/README.md)
retain exact opcode, native-call and detailed edge observations while
selected writes or I/O remain in order. The [ordinary call ABI
receipt](../../artifacts/benchmarks/call-abi-v086/README.md) shows the
separate benefit of making suspension exceptional to the one canonical
dispatcher rather than a permanent state machine for every call.
