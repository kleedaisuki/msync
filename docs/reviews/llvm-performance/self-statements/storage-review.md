# Self-statement: independent scalar/storage reviewer

## My assignment and actual participation

I was assigned an independent review of machine-scalar integration and ordinary Native value/storage layouts. The parent explicitly supplied the measured control/candidate results and asked whether representation changes could add ordinary Number/Bool/String/Path overhead despite zero machine-arithmetic counters. I was prohibited from production edits, builds, new timing runs and delegation.

I did not design or implement the original machine/storage changes. I cannot report their authors' motives, original requirements discussions or private trade-offs. My contribution was source inspection, reading existing diagnostic artifacts, sending findings to the parent, and writing `artifacts/diagnostics/llvm-sroa/reviews/scalar-storage.md`. This self-statement is another documentation-only contribution.

## Inherited context and assumptions

I accepted the supplied paired timing receipts as the measurement context rather than rerunning them. I accepted the brief's identification of already-fixed constant materialization, carrier copies, environment schema and FunctionComponent dispatch defects, and did not reopen them as current findings. I relied on the foundations receipt's statement that Native machine scalars remained outstanding to help place this integration after the control.

I inspected the working source and its diff, not an independently reconstructed complete source tree for each frozen executable. That distinction matters: I can identify code changes and their reachable paths, but I have not proven an instruction-by-instruction binary delta for every finding. The parent's final synthesis should retain that distinction.

## My review choices and rationale

I started with the assigned brief and relevant internal design notes. I then inspected NativeRoot, NativeValue, NativeDatum, NativeType, generic storage, direct scalar storage, frame access, memory copies and target pointer/load lowering. This scope was chosen to distinguish an actual representation-wide cost from an expensive operation that the measured workloads did not execute.

I explicitly checked root overlay and struct fields before making allocation claims. The new root alternatives overlap at offset zero; NativeValue and NativeDatum retain their prior instance fields. Consequently I rejected an enlarged universal value carrier or ordinary scalar boxing explanation.

I prioritized initialization maintenance because the diff replaced single-flag stores with span fills in reachable ordinary copy/store helpers. I followed the read side rather than treating the broader marking as automatically necessary: ordinary readiness and the explicit machine-read API inspect the starting flag, raw tier stores still maintain one flag, and current target lowering does not expose arbitrary interior byte offsets inside an ordinary field. I also examined the property test claiming whole-double initialization: its view starts at offset zero, so it does not distinguish single-flag marking from full-byte marking.

I used existing opcode and fallback counts to show exposure, not to manufacture execution time. I called out that raw generated scalar paths bypass the managed helper, so total Copy counts are not Fill invocation counts. I ranked generic machine-type checks second and eager source-type metadata third because their source-level additions were visible but their marginal effect was less well established.

## What I established, and what I did not

Established by source inspection:

- Ordinary generic storage writes/copies now perform broader initialization marking; Number/Path/Function/Address specialized managed stores also changed marking width.
- Current readiness consumers generally inspect a view's starting flag, not a full-width initialization scan.
- Source-only paths can encounter additional machine-discrimination tests despite zero machine arithmetic.
- Root overlays and existing ordinary physical representations did not acquire extra per-slot roots or a larger NativeValue/NativeDatum field set.

Established only as existing reported observations: the paired performance changes, opcode counts, raw tier counts and allocation deltas. I extracted some counts from the supplied JSONL files, but did not produce new measurements.

Not established: the elapsed-time contribution of any storage change, the exact number of changed helper calls in each workload, AOT inlining/code-generation consequences, the allocation-delta source, or the result of reverting initialization maintenance. I did not establish a new storage correctness defect. My recommended experiment remained a proposal.

## Possible mistakes and limitations in my report

The phrase “concrete, high-frequency incremental expense” can sound stronger than my evidence warrants. A more precise summary is “confirmed additional source-level maintenance on frequently reachable ordinary paths; frequency by storage format and marginal machine-code/runtime cost are unmeasured.” Span.Fill may become efficient wide stores or optimized helpers, so byte width is not proportional to elapsed time.

My proposed single-flag experiment needs a sharper compatibility boundary. Leaving explicit machine APIs untouched does not by itself preserve all existing API behavior: a caller can write through an ordinary Number API and then read an interior machine view. Changing the ordinary writer would change that sequence. The current measured target's lack of interior field lowering is strong workload-specific evidence, not proof that public NativeStorage callers can never depend on it. The parent should distinguish a compiled-frame/internal-path experiment from a universal public API change and preserve existing externally observable raw-view behavior.

I did not disassemble the frozen executables to prove added loads/branches survive AOT optimization. I therefore should not imply that each source predicate is one extra executed machine branch. Likewise, two additional NativeType reference fields are a metadata-layout change, not evidence that metadata dominates execution allocations.

The third finding is a low-priority potential cleanup rather than an established performance defect. Repeated union construction was not measured. A cache or lazy projection might introduce its own cost; no new machinery is justified by this review alone.

## Corrections the parent should make in the final synthesis

1. Attribute this to an independent reviewer, not an author explaining original implementation decisions.
2. Present widened initialization as the best isolated storage experiment, not a demonstrated cause of the measured percentage regression.
3. Do not translate opcode counts into Fill counts or sum them with raw tier operations as disjoint work.
4. Preserve direct C# raw/interior-view behavior when evaluating a source-carrier fast path; do not claim that keeping the explicit API method bodies unchanged is sufficient compatibility protection.
5. Keep “no carrier enlargement/ordinary boxing” explicit, so the diagnosis does not drift into an unsupported allocation story.
6. Treat machine branches and eager projections as secondary hypotheses until an isolated experiment or generated-code evidence discriminates them.
