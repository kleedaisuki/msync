# HPIR implementation and acceptance plan

2026-11-22 · Implementation and delivery gates. [HPIR](hpir.md) is the target design for
the bound `.mexe`. The [XIR evidence audit](executable-machine-ir.md),
[representation audit](xir-representation-audit.md), and
[0.9.3 runtime attribution](../benchmarks/0.9.3-runtime-attribution.md)
are the evidence base, not parallel implementation proposals. See the
[operation inventory](hpir-operation-inventory.md) and
[frozen 0.9.3 controls](../benchmarks/hpir-0.9.3-baseline.md).
The [source-to-physical journey](../../tests/HpirPipeline/README.md)
exercises the development candidate; [machine block evidence](../benchmarks/hpir-machine-blocks.md)
separates the interpreted and emitted forms of the same image.

## End state

```text
LLVM-derived optimization with msync producer contracts
  -> portable .mso
  -> select contextual effects and final representations
  -> one physical HPIR program in .mexe
  -> direct guest execution on a VM-owned native stack and heap
```

- **Before HPIR:** Keep SSA, typed places, exact allocation layouts, selected
  call targets, capture modes, producer shapes, and readiness/effect facts
  where they help compilation. Do not erase facts and make binding infer
  them back from generic addresses, strings, or bit patterns.
- **In HPIR:** Only specialized primitive computation, pointer arithmetic
  and typed memory operands, calls, and structural control flow. A true
  field read/write is a physical access. An intermediate result need not
  become a slot. Aggregates lower to concrete layouts, operations or helper
  calls; they do not introduce a second generic value model.
- **At execution:** Ordinary guest CALL/RET uses the guest stack pointer and
  a fixed frame ABI; allocations and collection use the guest heap. Host
  wrappers belong to actual host calls and embedding boundaries. Capacity,
  demands, actual dynamic alternatives and failures remain runtime events.
  They are not reasons to re-prove known layouts or frame ownership.
- **At binding:** Observe selected environment facts and emit prepared or
  residual effects. Frozen member lists and locations are not replanned at
  submission; live bytes, results, failures and execution admission remain
  live. Static language facts and contextual observations have separate
  producers. Both reach HPIR without a second general source optimizer.

The goal is to delete the permanent generic layer, not to admit more
programs to a special path beside it. A development-only candidate can
coexist with the frozen comparison image. A shipped HPIR executable has
one execution model, not an old-program fallback.

### v0.9.5 delivery boundary

The v0.9.5 working tree selects HPIR for source, detached objects,
executables and the REPL, and can promote the same physical image to
native code without restarting its guest computation. The old executable
codec, lowered instruction stream, guest interpreter and boxed runtime
value model are removed. The [installed Linux x64 package](../../artifacts/validation/hpir-v095-release-audit/README.md)
passes the existing platform journeys, with [matched v0.9.3 timings](../benchmarks/hpir-v095-default.md).
The Native AOT ARM64/x64 cross-artifact replay passes. The Windows x64
Native AOT package passes smoke, the public HPIR CLI journey, and the
detached NTFS gate on the same executable in interpreted and forced x64
modes. See [platform admission](../benchmarks/hpir-platform-gate.md).
In particular, a scalar-ready return type does not prove the
callback that produces it cannot suspend on host work.

The mounted ABI is exercised by the [ordered-container controls](../benchmarks/hpir-ordered-containers.md)
and all twelve original contextual artifact cases
([replay](../../artifacts/validation/hpir-contextual-cli-20261122/REPLAY.md)).
The contextual matrix includes actual bind-time metadata, frozen directory
membership, live file bytes, symbolic-link replacement, typed transfer
receipts, and retry after failure. It does not bypass or replace the
product cutover gate.

The [source-cutover admission matrix](hpir-cutover-admission.md) records
the concrete frontiers resolved on original CLI journeys, separately
from baseline failures. The fixed final corpus has 23 exact matches and
one inherited 0.9.3 discrepancy, preserved in the receipt rather than
silently approved or removed. Keep binding ownership and
runtime resource leases explicit for mixed prepared and residual effect
programs, where either order can produce a real dependency.

The earlier source/REPL shadow properties have been removed. Use the
normal Native AOT product build and keep the same binary for the
retained source, detached and session journeys. No path decodes or executes
an older Native artifact.

## Phases and dependencies

```text
0 Evidence and contracts
  -> 1 Producer / detached artifact boundary
  -> 2 Physical guest execution
      -> 3A Layouts, closures and calls
      -> 3B Typed futures and Range
      -> 3C Contextual effects and resource ownership
  -> 4 Default .mexe delivery and retirement of replaced paths
```

Phase 1 defines the interfaces used by all three phase 3 tracks. Phase 2
must already exercise one real escaping mutable capture and one prepared
effect; the tracks complete their coverage, not introduce their essential
semantics for the first time. The tracks share the same call convention,
heap tracing and work ownership rather than independently inventing them.

### 0 Evidence and contracts

- Freeze the packaged 0.9.3 image and a matched reproducible rebuild.
  Record toolchain, artifact digests, command and working-directory policy,
  JIT mode, corpus seed, scale, distribution and raw paired samples.
  Reuse existing receipts unless a new comparison needs fresh data.
- Keep contract oracles separate from released behavior where they differ.
  The O2 pending-identity mismatch is a known compiler defect, not a
  compatibility result to reproduce. The one-code/two-environment closure
  probe and the distinct-code dispatch control are separate cases.
- Inventory the current instructions and builtins actually produced.
  Classify each as a primitive, a physical access, a fixed helper call,
  a genuine dynamic branch/dispatch, or obsolete compiler machinery.
  Include REPL roots, cleanup edges and host interop; do not infer coverage
  from an opcode count.

**Exit:** A reviewable map from produced operations to semantic
obligations, with reproducible baseline/oracles and named gaps. No
hard-coded nanosecond targets inferred from the C stand-in.

### 1 Producer and detached artifact boundary

- Specify, for every fact needed by HPIR, its producer, where transforms
  preserve or invalidate it, and how detached `.mso` supplies it.
  Reuse information already present. For genuinely missing information,
  make an explicit producer and format decision; do not require source,
  replay it, or reverse-engineer the old instruction stream at bind/run.
- Make future-capable operations explicit before the LLVM pass that could
  erase their identity. Use ready-only primitives where proved; preserve
  distinct evaluations, identity-preserving copies and existing demand
  points. Check optimized behavior after the complete pipeline, not just
  after disabling the first failing pass.
- Define the physical HPIR instruction and metadata contracts: fixed
  carriers, object/field layouts, call entries and argument/result ABI,
  frame extent, relocations, roots and unwind data, and source mappings.
  Define the portable/bound format policy before the public switch.

**Exit:** A source-free `bind-ir FILE.mso` design with a demonstrable
producer for every essential fact, and a corrected pending-identity
oracle at supported optimization levels. No promise to change an
existing artifact format until the compatibility decision is made.

### 2 Physical guest execution

- Bring up a test-only end-to-end candidate through compile, detached bind,
  load, and execute. The image has one instruction stream. Reuse the
  existing [native frame stack](../../src/Msync.Runtime/NativeModel/NativeFrameStack.cs)
  and [native heap](../../src/Msync.Runtime/NativeModel/NativeGuestHeap.cs)
  where their ownership fits. Remove dependence on per-call managed frame
  views and per-allocation CLR descriptors from the physical path rather
  than adding another tier eligibility chain.
- Make guest CALL/RET the ordinary path, including a fixed non-leaf:
  use the finalized aligned frame extent, advance SP, and restore its
  previous value and continuation on exit. Handle real segment growth,
  recursion, live roots across calls, return and failure cleanup. Put
  escaping cells on the heap; no work retains an address
  into a popped frame. Keep root scanning and host references explicit
  at the appropriate boundaries, not at every read or write.
- The vertical control is not just pure arithmetic: include the block
  swap, shared-field read before a mutating call, an escaping captured
  location, a live reference across collection, a child frame crossing a
  stack segment, and a detached prepared transfer. Execute HPIR without
  relying on a JIT rescue path. A candidate that bails out to the old guest
  engine does not pass this milestone.

**Exit:** Exact end-to-end results and causal evidence of ordinary
guest-to-guest frames without managed wrappers or per-call ABI validation.
Actual segment growth, exceptional exit, collection and closure lifetime
must be exercised, not inferred from the happy path.

### 3 Complete the native representations and effects

| Track | Work to finish | Preserve and demonstrate |
| --- | --- | --- |
| **3A Layouts, closures, calls** | Carry concrete layout through fields, parameters, returns and captures; compile fixed field offsets and entry/environment call contracts. Pass a varying environment directly for known code. Use a finite/open dispatch only for genuine target variation. | Alias mutation, distinct and shared captures, callee-before-arguments, live callable replacement, recursive and non-leaf calls, same-code/two-environment versus two-code controls, REPL persistence and reset. |
| **3B Typed futures and Range** | Use one work-ownership protocol with concrete completion layouts and typed helpers. Keep ready arithmetic and synchronous host builtins out of generic async dispatch. Retain known Range producer shape for cursor or loop selection and fusion. | Pending operation identity and branch demand, cancellation and failure order, no lost cleanup, literal snapshots and alias identity, effectful callbacks only on consumption, the 0/2/1 callback sequence across demand steps, bounded and live stream execution. |
| **3C Contextual effects** | Emit prepared/residual operations and dependencies through the same executor conventions; remove general bind-time guest inlining/reproof from the HPIR route where its work is replaced. Consume bound resource metadata directly. | Source-free artifact export/run, frozen selection membership with live bytes, no work published at bind, no planning on run, side effects after opaque calls, directional dependencies, cancellation/cleanup and concrete filesystem/transport diagnostics. |

For all three tracks, specialize code only when the relevant field offsets,
payload ABI, behavior or call convention differ. A concrete layout identity
does not automatically require another host generic or duplicate machine
body. Dynamic closures, structural projections and live streams still
execute natively via the bounded part of the model; they are not an excuse
to retain the old general executor.

**Exit:** The inventory from phase 0 is covered or consciously retired
under an explicit compatibility decision. End-to-end checks use actual
installed artifacts and include hard cases, not only statically closed inputs.

#### Preserve deferred boundaries in the real compute corpus

Signal, polygon, and numeric-record fixtures read their size/seed from
nullable stdin. They cannot be represented by replacing their input
with a constant or by awaiting it at invocation just to select a
scalar ABI. Use the original fixtures to discharge these connected
contracts:

1. The actual null/control observation can demand its typed result,
   while unobserved read/parse values keep their completion identity.
2. A genuinely mixed ready/deferred return has a typed payload and
   future edge across the selected call ABI. A mutable captured
   location has one concrete carrier for all admitted stores and a
   matching heap tracing layout. Neither can be recovered by scanning
   arbitrary f64 bytes.
3. If a numeric sequence has deferred bounds, construction must not
   demand them early. When its cursor is opened, the bounds are
   demanded as required by consumption; afterwards its Number items
   are ready. A ready sequence must not inherit a pending-item
   representation just because another source Range actually retains
   pending elements.
4. A literal vector snapshots membership, not mutable objects. Its
   cursor must root the allocation through calls/collection and use
   the producer-selected item layout at each pull.

Time only after source and bound artifact behavior match the original
corpus oracle. The ready-signal derivative is a mechanism control, not
a substitute for any of these gates.

### 4 Deliver the default and retire replaced machinery

- Keep the supported CLI journeys intact: source run, portable run,
  `bind-ir`, `compile --prepare`, disassembly and detached prepared run.
  Version any required encoding change explicitly and follow the agreed
  recompilation/compatibility policy. Do not silently reinterpret older
  layouts or rewrite published assets.
- Validate supported platforms and installed Native AOT packages through
  the repository's focused journeys and platform acceptance. Check
  telemetry, diagnostics, real output, cancellation and resource cleanup,
  not just process exit. Preserve source/function/operation identities
  across lowering for debuggability.
- Switch production execution once the same candidate clears semantic,
  mechanism and performance gates. Remove the replaced generic paths from
  the production pipeline, including the duplicate executable stream and
  whole-function veto where its causal facts no longer exist. Keep only
  the actual native runtime services used by HPIR. Frozen old images
  remain measurement controls, not runtime fallbacks.

## Gate evidence

The acceptance cohort is fixed: the [frozen original CLI cases](../../artifacts/validation/hpir-cutover-wire7/cohort-1795369654/REPLAY.md),
the in-tree HIR/source, public session and platform journeys already named
in the [admission record](hpir-cutover-admission.md), and the listed paired
0.9.3 workloads. Re-run only affected members while working on the
cutover and the full fixed cohort once on the candidate. Extend a small
mechanism control only when an existing journey cannot distinguish the
observed failure. Old test assertions about Native2 opcodes or magic
describe the replaced implementation; keep language behavior and the
explicit artifact recompilation contract as the oracles.

Use the existing [concurrent journey runner](../../tests/TestExecution/README.md)
and [workload corpus](../../tests/Performance/Corpus/README.md). Select
contract journeys by change and expand to release scope at the cutover.
Prefer byte/stdout/identity/effect-order oracles to opcode unit counts.
The maintained entry points are the [HPIR source pipeline](../../tests/HpirPipeline/),
the [physical VM contracts](../../tests/HpirRuntime/), and the
[contextual I/O journeys](../../tests/Msync.EndToEnd/contextual_io_journey.py).

| Question | Workloads and comparisons |
| --- | --- |
| Did binding stop removing native eligibility? | Pair `.mso` and `.mexe` on signal and polygon, and record bind and run separately. Check native calls, bridge reasons and representation decisions. |
| Did the guest ABI replace the bridge? | Fixed nested calls, recursive depth, escaping captures, one-code/two-environment and genuine multi-target cases; then B+ and live-policy. Count frames, segment growth, argument copies, descriptor creation and host transitions. |
| Did native representations cover real work? | Numeric-record-stream and bounded-sensor pipeline for Range, Set and B+ for layouts/heap, unicode-path-manifest for direct host calls. Keep real I/O and short commands as controls. |
| Did the change preserve system-wide behavior? | Detached I/O, cancellation, genuine aliases, lifecycle and packaging; telemetry off/on comparisons, bounded memory and latency. Use the reference and contrast distributions where they can distinguish a claimed mechanism. |

The first performance criterion is mechanistic: the promised wrapper,
copy, repeated proof or dispatch is actually absent from the relevant
path. The second is workload performance with matched paired runs:
capture bind time, cold/startup and execution separately, and do not
accept a demonstrated material regression without a workload-specific
explanation and decision. The measured losses from simply disabling
bind inlining on live-policy and B+ are explicit controls. No pooled
geomean can hide one of those regressions.

Extend [sink-neutral observability](observability-architecture-v086.md) at
each changed phase. Record producer decisions, unknowns that actually
remain, rewritten work and rejected choices at compile/bind; at runtime
record real reservations/growth, heap publication and GC work, typed
calls/demands, and causal effect ownership and failure. Preserve useful
function/source/resource context. Time with recording off, collect
detailed causal traces in separate runs and do not put a recorder or
per-operation allocation on the disabled path.
