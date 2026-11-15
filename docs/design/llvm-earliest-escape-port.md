# Earliest capture analysis for function-local DSE

This component translates the pinned LLVM
`a55e64432a516fd6507d3ecfba7407cfe425f940` capture-before query. It is an optional
optimizer analysis used by DSE/AA, not a runtime admission gate, global heap
model, or a new allocation/capture promise for project operations.

## Source map

| Managed slice | Pinned source |
| --- | --- |
| `LlvmCaptureTracking.Earliest.cs`: EarliestCapturesTracker / FindEarliestCapture | `Analysis/CaptureTracking.cpp`: EarliestCaptures, FindEarliestCapture |
| `LlvmEarliestEscapeAnalysis.cs` | `Analysis/BasicAliasAnalysis.cpp:227–289`; `Analysis/AliasAnalysis.h`: EarliestEscapeAnalysis |
| Instruction nearest common dominator overload | `IR/Dominators.cpp:309–325` |
| Optional CycleInfo reachability branches | `Analysis/CFG.cpp`: isReachableImpl and block/instruction isPotentiallyReachable |

The existing shared capture walker retains its 150-use default budget. Earliest
tracking visits all captures rather than stopping at the first one. It joins
non-return capture instructions using the real instruction nearest common
dominator: same-block order, unreachable-block handling, then the common block's
terminator when neither capture dominates the other. Return captures contribute
to WithRet but not the earliest non-return escape. On the source use limit, both
component masks become the requested mask and the earliest point is entry's
first instruction.

## Query state and mutation

Each function-local EarliestEscapeAnalysis owns two source caches:

- Object → earliest instruction plus return/non-return capture components.
- Earliest instruction → affected objects, for RemoveInstruction invalidation.

A query on a non-identified local object retains the existing provenance result;
this component does not infer new HeapAlloc/noalias attributes. No capture means
no capture before any context. With a context, an escape that cannot reach that
context contributes no capture. For the same instruction, OrAt includes the
escape; excluding it is useful only when the instruction is not in a cycle.
The real CycleInfo result is used when supplied, otherwise the existing bounded
CFG/LoopInfo query is used. The reachability adapter retains LLVM's 32-block
budget, cycle-over-loop precedence, exclusion holes, cycle exits and acyclic
backward-dominator pruning. It does not replace irreducible cycles with natural
LoopInfo or a custom SCC implementation.

The source longjmp rule uses actual standard `returns_twice` flags on calls and
callees. Function scanning is lazy and cached; no function-name guessing is
performed. DSE clients call RemoveInstruction for erased instructions and keep
the supplied dominator/cycle analyses valid for their mutation scope.

## Observability and validation boundary

Optional metrics count tracker visits, use-limit stops, cache hits/misses,
reverse invalidations, returns-twice scans and context reachability work. The
existing shared capture/CFG instrumentation supplies the actual use/edge work;
recording-disabled paths do not allocate event dictionaries.

The four source files are published under `src/Msync.Optimization/Llvm/` with
CycleInfo and the AA CaptureAnalysis injection dependencies present. Validation
is delegated to the root-serialized coherent build; no independent build or
execution claim is attached to this source checkpoint. Relevant cases include
return-only captures, disjoint-branch captures, same-instruction queries inside
and outside cycles, returns-twice re-entry, budget exhaustion and invalidation
after deleting the earliest capture. These are analysis cases, not substitutes
for DSE's real source execution acceptance.
