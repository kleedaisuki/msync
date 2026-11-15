# Interprocedural optimization: upstream provenance audit

## Status and inspected revisions

This is an audit, not a delivered optimization implementation. The preceding
Native IPA prototypes were original work and must **not** be represented as
ports of LLVM or GCC. The active goal now requires LLVM IR as the primary IR and
strictly traceable existing algorithms. No LLVM backend, target machine,
optimization pipeline or release dependency is selected by this note.

Primary inspected source is the root-selected llvm-project snapshot
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940),
also recorded in `vendor/llvm-reference/SOURCE.json`. All LLVM links below pin
that commit, not moving `main`. Supplemental source comparisons use GCC 15.2
commit [`5115c7e447fc07457443df874bf57840e8316d5f`](https://github.com/gcc-mirror/gcc/tree/5115c7e447fc07457443df874bf57840e8316d5f)
and OpenJDK javac 25 commit
[`6c48f4ed707bf0b15f9b6098de30db8aae6fa40f`](https://github.com/openjdk/jdk/tree/6c48f4ed707bf0b15f9b6098de30db8aae6fa40f).
These are comparison snapshots, not additional product dependencies or version
selections. Temporary source copies were inspected under
`.temp/native-midend/upstream-ipa/`; this document retains their reproducible
source locations and algorithm correspondences.

## Prototype inventory and verdict

The following paths identify drafts as inspected before root-owned quarantine;
they are not accepted production stages:

| Draft | What it actually does | Upstream correspondence | Verdict |
| --- | --- | --- | --- |
| `NativeInterproceduralGraph.cs` | Builds integer-index direct call edges, computes finish order, then traverses reverse edges to find SCCs | General SCC concept only | Original two-pass Kosaraju-style implementation; **not** LLVM's Tarjan SCC iterator or LazyCallGraph |
| `NativeInterproceduralValues.cs` | Tracks literal constant indices and explicit closure construction within one basic block; drops those facts at block boundaries | Very limited local fact tracking | **Not** LLVM IPSCCP, GCC IPA-CP, or a cross-function value-lattice implementation |
| `NativeInterproceduralOptimization.cs` | Finds syntactically unread/unwritten parameters, traces branch operands backward, clones slot-array functions and injects constant assignments under a custom instruction-count budget | Motivated by dead-argument elimination and specialization | Original bounded heuristic, **not** a direct implementation of either upstream algorithm |
| Referenced `NativeTailRecursion.Run` | A proposed call in the orchestrator | Intended tail-recursion elimination | No `NativeTailRecursion.cs` implementation was written; no transformation or acceptance evidence exists |
| Existing runtime tail trampoline | Transfers eligible calls to fresh activations and retains actual cleanup/return ownership | Tail execution mechanism | Runtime machinery, not LLVM's compiler tail-recursion-to-loop transformation |

The only product-path cleanup performed for this audit removed the draft's sole
`ImportCapture` reference. Root owns archiving/excluding the unintegrated drafts;
`NativeInterproceduralOptimization.cs` also referred to the never-written tail
class and to the now-rejected `DirectCall` prototype. It must not be integrated
as though it were a compiling, tested pass. No builds were run for this audit.

## 1. Call graph and SCCs

### Concrete LLVM algorithms

- [`SCCIterator.h`, algorithm and iterator state](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/ADT/SCCIterator.h#L7-L44):
  Tarjan DFS with an internal visit stack, visit numbers and low-link values;
  SCC iteration is in reverse topological order of the SCC DAG.
- [`DFSVisitOne` / `GetNextSCC`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/ADT/SCCIterator.h#L150-L212):
  concrete state transitions and component extraction. The Native draft instead
  performs two complete graph traversals and explicitly builds reverse edges.
  Both algorithms compute SCCs, but that is not source provenance equivalence.
- [`CallGraph::addToCallGraph`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Analysis/CallGraph.cpp#L74-L110):
  external linkage, address-taken functions and calls leaving the module have
  explicit external-node relationships. A Boolean `DynamicCalls[function]` is
  not an implementation of these relationships.
- [`LazyCallGraph` reference edges and RefSCCs](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Analysis/LazyCallGraph.h#L113-L127)
  and [the SCC/RefSCC containment contract](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Analysis/LazyCallGraph.h#L413-L540):
  references to a function and actual calls to it are distinct. Inlining and
  devirtualization can change their relationship. The draft has no RefSCC or
  graph mutation/update algorithm.

### Missing correspondence

The Native draft did not cover the new source-only-call experiment and still
recognized only the old generic `Call` opcode. More fundamentally, the old
Native `Call.B` is a specialization hint: the runtime may first dispatch an
actual native descriptor. It is not equivalent to a direct LLVM call operand.
An LLVM-IR migration must make the actual callable operand and environment
ordinary explicit IR values. It cannot obtain a valid upstream call graph by
renaming the integer hint or introducing a custom capture-import instruction.

Required distinctions in future acceptance: direct cycles, unknown indirect
calls, function address/reference without a call, callbacks and recursive SCC
updates after a call target changes. The draft provides none of the upstream
mutation/RefSCC acceptance evidence.

## 2. Dead arguments and return values

### Concrete LLVM algorithm

[`DeadArgumentElimination.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp)
implements a module-level liveness dependency algorithm, not a search for
parameters absent from instruction operands:

1. [`surveyUse` / `surveyUses`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp#L355-L457)
   collect whether uses are live or depend on another argument/result's liveness.
2. [`surveyFunction`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp#L462-L628)
   establishes ABI eligibility and argument/return dependencies. Non-local or
   signature-constrained functions are frozen; these are actual external ABI
   contracts, not source-program validation gates.
3. [`markLive`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp#L674-L706)
   propagates liveness through those dependencies.
4. [`removeDeadStuffFromFunction`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp#L709-L876)
   rebuilds the function type and call sites, including return-value changes.
5. [`run`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/DeadArgumentElimination.cpp#L1085-L1120)
   separates surveying from rewriting. Recursive forwarding of a dead argument
   is handled by assuming dead until a real live dependency is discovered.

### Draft deviations

The draft marks any syntactic read **or write** of a parameter as live, so an
argument passed only to another dead argument is retained. It excludes recursive
functions rather than implementing dependency propagation through recursion. It
has no dead-return-value implementation. It retains the old callable ABI and
creates per-call private clones instead of implementing the upstream module
liveness/signature rewrite. Its frame-geometry exclusion belongs to the old
Native slot-array ABI, not to LLVM's argument liveness algorithm.

GCC offers another concrete algorithm, not permission to treat this draft as a
port: [`ipa-sra.cc`, `ipa_sra_analysis`](https://github.com/gcc-mirror/gcc/blob/5115c7e447fc07457443df874bf57840e8316d5f/gcc/ipa-sra.cc#L4411-L4593)
propagates per-parameter/access summaries in call-graph order and determines
parameter/result adjustments. The draft contains no such access-summary or
aggregate-parameter adjustment implementation.

Future acceptance must include transitive and recursive dead forwarding,
unused return components, retained external/escaping callable ABIs, and continued
evaluation of effectful argument expressions even after the call no longer
passes their resulting values.

## 3. Constant propagation and specialization

### Concrete LLVM algorithms

- [`SCCP.cpp`, `runIPSCCP`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/SCCP.cpp#L104-L160)
  registers trackable arguments/returns/globals with `SCCPSolver`, tracks
  executable blocks, solves lattice facts, and then drives specialization.
- [`FunctionSpecializer::run`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/FunctionSpecialization.cpp#L608-L746)
  collects, estimates and selects specializations; the implementation uses
  target-cost and block/latency information rather than a fixed Native bytecode
  instruction-count allowance invented for this project.
- [`isCandidateFunction` / `createSpecialization`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/IPO/FunctionSpecialization.cpp#L979-L1038)
  retain eligibility boundaries, avoid recursively specializing generated clones,
  and initialize specialized clone argument lattice values. The clone becomes
  internal linkage and participates in the solver.
- [`CloneFunction` / `CloneFunctionInto`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/CloneFunction.cpp#L307-L440)
  use value mappings and maintain LLVM IR metadata/attributes/debug identity.

### Draft deviations

Block-local literal and closure maps are not executable-edge/lattice solving.
Backward tracing from branch operands is not IPSCCP. Injecting `Constant` writes
into mutable parameter slots and copying PC arrays is not LLVM SSA cloning.
The 256/768 instruction budgets, recursive-function exclusion and clone-key
format were project-invented choices, not copied upstream decision policies.
No cost/benefit or execution evidence was produced for them before the goal
changed. LLVM cost hooks do not authorize selecting a backend or target here;
that remains outside the approved task.

For comparison, GCC's
[`ipcp_driver`](https://github.com/gcc-mirror/gcc/blob/5115c7e447fc07457443df874bf57840e8316d5f/gcc/ipa-cp.cc#L6462-L6499)
separates interprocedural propagation from the cloning decision stage.
[`ipcp_propagate_stage`](https://github.com/gcc-mirror/gcc/blob/5115c7e447fc07457443df874bf57840e8316d5f/gcc/ipa-cp.cc#L4047)
and the surrounding code operate on explicit call-graph summaries and value
lattices. That is not implemented by the draft's per-block constant dictionary.

Acceptance must distinguish a constant passed through reachable phi/control-flow
edges from a literal immediately adjacent to a call, and must include escaped
callables, recursive callers, closure environments, exact floating-point bits
and code-growth decisions justified by the actual selected upstream algorithm.

## 4. Tail recursion

No compiler tail-recursion implementation was completed in the Native draft.
The existing runtime trampoline preserves fresh activation/cleanup semantics;
its name and successful deep-recursion journeys do not establish LLVM TRE
coverage.

The actual LLVM transform is
[`TailRecursionElimination.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/TailRecursionElimination.cpp):

- [`canTRE`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/TailRecursionElimination.cpp#L141)
  and `markTails` separate eligibility from actual recurrence rewriting.
- [`createTailRecurseLoopHeader`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/TailRecursionElimination.cpp#L722-L773)
  creates a new entry, a recurrence header and argument PHIs; it updates the
  dominator structure. PHI incoming values represent simultaneous next-iteration
  arguments, not sequential mutation of the old activation's shared cells.
- [`canTransformAccumulatorRecursion`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/TailRecursionElimination.cpp#L636-L677)
  checks actual operation associativity/commutativity. It is not permission to
  reassociate msync's ordinary binary64 addition/multiplication.

GCC's concrete counterpart is
[`find_tail_calls`](https://github.com/gcc-mirror/gcc/blob/5115c7e447fc07457443df874bf57840e8316d5f/gcc/tree-tailcall.cc#L575)
and [`eliminate_tail_call`](https://github.com/gcc-mirror/gcc/blob/5115c7e447fc07457443df874bf57840e8316d5f/gcc/tree-tailcall.cc#L1594)
in `tree-tailcall.cc`. Its GIMPLE/CFG rewriting and argument-copy decisions are
not the never-written Native loop emitter.

For an LLVM-IR implementation, shared captures are ordinary explicit environment
pointers/loads/stores and source callable identity remains a frontend semantic
obligation. No custom capture-import opcode is needed or authorized. Acceptance
must cover argument swaps, different selected closure environments, captured
fresh locations, escaping locals, actual pending process/transfer ownership,
active iterators and regions, and return continuations that contain conversions
or observable writes. Calls whose lifetime cannot be replaced by a loop retain
ordinary calls; no runtime reconstruction of a deleted compiler contract is a
substitute for the representation change.

## 5. What javac does and does not establish

At the pinned javac revision,
[`Gen.visitApply`](https://github.com/openjdk/jdk/blob/6c48f4ed707bf0b15f9b6098de30db8aae6fa40f/src/jdk.compiler/share/classes/com/sun/tools/javac/jvm/Gen.java#L1913-L1947)
generates the method expression, arguments in their external parameter types,
and the invocation. It is a useful concrete source for invocation lowering and
evaluation order. It is **not** an implementation of general call-graph IPA,
LLVM IPSCCP, dead-argument elimination or TRE. HotSpot runtime/compiler algorithms
must not be attributed to javac or silently imported as authorization for a JVM
backend. No javac middle-end port is claimed here.

## Integration disposition

1. Keep the Native IPA drafts outside the production compilation graph. Their
   presence, names, telemetry labels and resemblance to textbook algorithms do
   not satisfy the current strict provenance requirement.
2. Use the explicit upstream LLVM IR entities and the selected upstream
   algorithm's actual state transitions and invariants. Record every necessary
   language adaptation separately from the port; do not call an original
   heuristic a direct port merely because its output resembles the same
   optimization.
3. Preserve externally established function/capture identity and resource
   semantics in ordinary IR representation. Optional transformations consume
   that representation; no new mandatory heap solver or custom "safety"
   instruction should be introduced to make a port appear applicable.
4. This audit provides source anchors and acceptance obligations only. It does
   not select or run LLVM's default optimizer pipeline, link a backend, claim
   implementation completeness, or change the active goal's completion status.
