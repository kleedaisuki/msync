# Scalar prototype provenance audit

## Scope and result

This audit follows the superseding user requirement: primary LLVM IR, with
optimization algorithms sourced precisely from LLVM/GCC/javac, not newly invented
variants. It does **not** select an LLVM backend, optimization pipeline, release
or package dependency. No product edits or builds were performed during this
audit.

**None of the Native scalar prototypes audited here is an exact upstream port.**
They were independently written implementations of broadly known SSA, SCCP and
CSE ideas. Similar terminology and compatible examples do not establish source
correspondence. In particular, `NativeScalarValues` is not LLVM GVN or NewGVN.
Its scoped dominance walk is conceptually closer to EarlyCSE, but it is not a
port of EarlyCSE either.

The drafts were removed from product compilation while this audit was running.
Their exact last state is retained under
[`artifacts/diagnostics/native-midend-drafts/source/`](../../artifacts/diagnostics/native-midend-drafts/source/),
with retirement reasons in
[`QUARANTINE.json`](../../artifacts/diagnostics/native-midend-drafts/QUARANTINE.json).
Local paths and methods below refer to that archive, not an active optimizer.
There is no claim of GCC or javac source lineage for these drafts.

## Immutable upstream source snapshot

All primary comparisons below use LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, matching
[`vendor/llvm-reference/SOURCE.json`](../../vendor/llvm-reference/SOURCE.json).
The comparison fetched only the listed source files. `llvmorg-20.1.8` was briefly
inspected first, but its line numbers and structures are **not** the basis for
the table below. This pin is a reproducible provenance reference, not a claim
that a particular backend or pass pipeline has been authorized.

| Upstream component | Pinned source and relevant anchors |
| --- | --- |
| IR values and def/use ownership | [`Value.h`, `Value`, use list, lines 75–173][value]; [`Instructions.h`, `PHINode`, line 2721 onward][phi] |
| SSA repair | [`SSAUpdater.cpp`, available-value map and public queries, lines 39–140; implementation dispatch, lines 343–354][ssa-updater] |
| SSA repair algorithm | [`SSAUpdaterImpl.h`, `GetValue`, lines 93–110; backward block construction, line 115 onward; dominators, line 233 onward; phi placement, line 293 onward][ssa-updater-impl] |
| Memory promotion | [`PromoteMemoryToRegister.cpp`, `isAllocaPromotable`, line 66 onward; live-in/IDF placement, lines 901–988; rename traversal, line 1242 onward][mem2reg] |
| SCCP lattice | [`ValueLattice.h`, lattice states, lines 26–76; `mergeIn`, line 407 onward][lattice] |
| SCCP solver | [`SCCPSolver.cpp`, state/worklists, lines 572–629; feasible-edge updates, lines 1304–1320; phi transfer, line 1450 onward; solver driver, lines 2260–2286][sccp-solver] |
| SCCP transformation driver | [`SCCP.cpp`, `runSCCP`, lines 54–121][sccp-driver] |
| Early CSE | [`EarlyCSE.cpp`, `SimpleValue`, line 84 onward; scoped tables, lines 513–585; `NodeScope`, lines 601–628; elimination, lines 1428–1457][early-cse] |
| Classic GVN | [`GVN.cpp`, expression representation, lines 112–143; expression construction, lines 596–632; leader lookup, lines 3401–3426; main instruction processing, line 3694 onward; PRE, line 4166 onward][gvn] |
| NewGVN | [`NewGVN.cpp`, congruence classes, line 248 onward; maps and touched values, lines 493–613; congruence updates, line 2336 onward; instruction value numbering, line 3091 onward][newgvn] |

## Exact correspondence and deviations

The verdict column distinguishes a recognizably shared algorithmic idea from a
source-derived port. Replacing an LLVM `DenseMap` with a .NET dictionary could
be a documented container adaptation; replacing LLVM values, worklist structure,
state domain and rewriting protocol with a different scheme is not merely a
container adaptation.

| Archived local code | Nearest upstream counterpart | Actual correspondence | Material deviation / port verdict |
| --- | --- | --- | --- |
| `NativeScalarGraph.Value`, `Kind`, `Inputs`, `Users` | LLVM [`Value`/`Use`][value], [`PHINode`][phi] | Both name definitions and their uses. | Local integer tokens include synthetic `Entry`, `External`, and `Clobber` kinds over mutable Native slots. LLVM IR directly owns typed SSA `Value`/`Use` objects and explicit phi incoming edges. The draft is an analysis overlay, **not primary LLVM IR** and not an IR class port. |
| `NativeScalarGraph.Define`, `Read`, `At` | [`SSAUpdater` queries][ssa-updater] | Both can answer which value reaches a use. | The draft pre-indexes Native slot writes, binary-searches instruction positions and recursively asks predecessor outputs. It does not implement `SSAUpdaterImpl`'s backward subgraph, dominator, phi-placement and available-value stages. **No direct source correspondence.** |
| `NativeScalarGraph.Entry`, `Out` | [`SSAUpdaterImpl::GetValue`][ssa-updater-impl], mem2reg's [IDF placement and rename][mem2reg] | A memoized placeholder breaks cycles and phi operands come from predecessors. | The draft creates an entry phi on demand for every requested block/slot, including single-predecessor cases; its own synthetic entry supplies a varying value. It has neither the upstream placement algorithm nor upstream poison/unreachable behavior. It is not LLVM mem2reg or SSAUpdater. |
| `NativeScalarGraph.Exposed`, `WideFrameAccess` | [`isAllocaPromotable`][mem2reg-promotable] | Both prevent treating genuinely shared memory as an ordinary scalar value. | The draft uses Native `FrameAddress`, capture/binding modes and a whole-function wide-frame gate. That gate is a project-specific policy, not the upstream structural alloca-use algorithm. It should not be reintroduced under an LLVM name. |
| `NativeScalarSccp.State`, `Lattice`, `Meet` | [`ValueLatticeElement`][lattice] | Unknown → constant → overdefined is a recognizable constant-propagation subdomain. | LLVM's actual domain also represents undef, not-constant and constant ranges, including range/undef merging. The draft has three states with Native bit-exact constant equality. It is **not** the upstream lattice with names translated. |
| `NativeScalarSccp.Edge`, `Phi` | [`markEdgeExecutable`][sccp-edge] and [feasible phi operands][sccp-phi] | A newly feasible edge revisits destination phis; phi merging ignores infeasible incoming edges. This is the closest semantic correspondence in the draft. | This common SCCP rule was independently implemented over local token arrays. It does not establish that surrounding solver or IR is an upstream port. |
| `NativeScalarSccp.Solve`, `Update` | [LLVM SCCP worklists][sccp-solver] and [solve loop][sccp-work] | Changed definitions schedule their users and newly executable blocks schedule instructions. | The draft combines everything in one FIFO queue with a queued bitmap and custom clobber/external nodes. The pinned LLVM source uses distinct basic-block and instruction worklists, visitor dispatch, invalidation handling and the LLVM value lattice. **Custom solver, not direct port.** |
| `NativeScalarSccp.Instruction` | [`visitBinaryOperator`][sccp-binary] and other LLVM transfer visitors | Fold when operands are constant; otherwise propagate unknown/overdefined information. | The draft recognizes only Native Constant/Copy/Move/Unary/Binary/MakeType and calls the project's host/Number folding helper. It omits upstream transfer visitors, value tracking, predicates, attribute facts and undef handling. No claim to the LLVM constant folder or full SCCP coverage is justified. |
| `NativeScalarOptimization.Function` | [`runSCCP`][sccp-driver] | Both rewrite constants and resolved branches after solving. | The draft substitutes PC-preserving Native Constant/Jump instructions and leaves unreachable code for a later compactor. LLVM replaces uses in its SSA IR, changes/removes infeasible edges and unreachable blocks with dominator updates, resolves undef behavior and removes SSA copies. This is a custom Native rewrite driver. |
| `NativeScalarValues.Run`, `Instruction`, `Restore` | [`EarlyCSE::NodeScope`][early-cse] and [simple CSE][early-elim] | Dominator-tree traversal and scoped expression availability have clear conceptual similarity. | The draft maintains lists of mutable-slot leaders plus an undo log, checks reaching-slot identity at every candidate, then emits Native Copy. EarlyCSE uses scoped tables over LLVM instructions/values, combines flags and replaces SSA uses before erasing instructions. **Not an EarlyCSE port.** |
| `NativeScalarValues.Expression`, `Key`, `Number` | [GVN expression construction][gvn-expression] and [leader lookup][gvn-leader] | An opcode/type/operand-value-number key and a dominating representative are standard CSE mechanisms. | The draft has a fixed three-operand key, bespoke phi-number collapse and native slot availability. It lacks upstream expression/attribute handling, call/memory value numbers and PRE. Calling it complete GVN would be inaccurate. |
| `NativeScalarValues` as a whole | [NewGVN congruence-class solver][newgvn] | Both seek equivalent computations. | The draft has no NewGVN congruence partitions, touched-instruction worklist, expression-to-class mappings, leader-change propagation or memory classes. **No substantive NewGVN implementation correspondence.** |
| `NativeScalarValues.Pure` | [`SimpleValue::canHandle`][early-simple] and instruction equality | Both restrict transformations to operations whose semantics permit them. | The Native primitive-kind whitelist is independently chosen. It does not implement the pinned upstream handling of constrained floating point, flags, calls and IR equality. There is no source-derived eligibility policy here. |

## Semantic boundary that cannot be hidden by a name change

The project's Number behavior, exact signed-zero/NaN payload observations,
source errors, pending datums and shared-location identities were the basis for
parts of the Native whitelist and tests. LLVM's typed constants, undef/poison,
IR flags and constrained-floating-point operations are a different explicit
semantic vocabulary. A port must make the language-to-LLVM-IR mapping explicit;
matching a few arithmetic outputs does not prove that host `double` folding and
LLVM constant folding are interchangeable. Conversely, inventing a new global
"safe enough" policy and calling it LLVM eligibility would not meet the current
requirement either.

The draft's `Move`/`Drop` clobber nodes and mutable-slot leader revalidation exist
because its primary representation is **not SSA**. Once LLVM IR is primary,
those Native representation repair mechanisms are not the LLVM scalar algorithm
and should not be carried into the new IR merely to preserve the prototype.

## What can honestly be reused

- **Behavioral cases:** conditional joins, loop-carried definitions, mutation
  snapshots, signed zero, NaN values, dominating duplicate expressions and
  invalidated leaders are useful language regression inputs. Their expected
  behavior must be exercised through the new real IR/product paths.
- **Not upstream tests:** `ScalarChecks.cs` uses a small local fixture evaluator
  and Native instruction-shape assertions. It is not copied LLVM regression
  coverage and cannot establish implementation provenance. No passing focused
  scalar-owner build/test receipt was produced before the goal changed.
- **Observation vocabulary:** node/phi/worklist counts, executable edges,
  rewrites and source/function ownership remain useful telemetry requirements.
  They are not optimization algorithms or authorization for this old pipeline.
- **Algorithm code:** no draft algorithm should be credited as an exact port.
  It can be retained only as a quarantined comparison artifact, not as a shortcut
  around selecting and translating the actual upstream implementation.

## Upstream entry points for a source-derived implementation

These are concrete source candidates, not a newly selected optimization pipeline:

1. Primary typed LLVM-style `Value`/`Use`, `Instruction`, `BasicBlock` and
   `PHINode` ownership must precede scalar algorithm integration. An opcode enum
   alone is not that representation.
2. If scalar promotion is required, choose the actual mem2reg or SSAUpdater
   implementation appropriate to the caller. Document its source stages,
   adaptation of types/containers and treatment of unavailable values. Do not
   substitute the draft's recursive token builder and relabel it.
3. A SCCP port must identify its pinned `ValueLatticeElement`, visitors,
   executable-edge state, worklist driver and IR rewrite driver. An omitted
   upstream facility requires an explicit, user-approved applicability scope, not
   a local reduction to three custom states.
4. EarlyCSE, classic GVN and NewGVN are distinct upstream implementations. A
   source-derived port must name which one is being translated and carry its
   actual expression identity, scope/leader or congruence mechanism and rewrite
   semantics. The draft's scoped CSE is not a substitute for all three.
5. Preserve the applicable upstream license/notice and commit/function mapping
   when source is translated. This audit itself does not vendor those algorithms
   or activate them in the product.

## Archived local fingerprints

All files below are relative to `artifacts/diagnostics/native-midend-drafts/source/`.
These hashes identify the inspected retired code even if the product tree moves.

| File | SHA-256 |
| --- | --- |
| `src/Msync.Optimization/NativeModel/NativeScalarGraph.cs` | `feb0ee8e2fdb7fad32cc15650d07dce76b3c4785d2595f6671974ecce0bab39a` |
| `src/Msync.Optimization/NativeModel/NativeScalarSccp.cs` | `a0941ab599cb33bc1c0a2cc20bc8a95d394de216492a554ad9d0fa7922386cda` |
| `src/Msync.Optimization/NativeModel/NativeScalarValues.cs` | `a75ebcc956c914db835195d8784ea3430c1a2fc253190a985c26ce0943be2c39` |
| `src/Msync.Optimization/NativeModel/NativeScalarOptimization.cs` | `c91b42aa7689897ff011846eeb3301f252b2170fa39a916dadb6d0a9cda4d81d` |
| `tests/NativeOptimization/ScalarChecks.cs` | `b97cd13cfae5871820a4c47ff2be644ce55ae79d781535f1fe852dd4ceb033be` |

[value]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/IR/Value.h#L75-L173
[phi]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/IR/Instructions.h#L2721-L2850
[ssa-updater]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SSAUpdater.cpp#L39-L140
[ssa-updater-impl]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Transforms/Utils/SSAUpdaterImpl.h#L93-L351
[mem2reg]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/PromoteMemoryToRegister.cpp#L901-L1034
[lattice]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Analysis/ValueLattice.h#L26-L76
[sccp-solver]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SCCPSolver.cpp#L572-L629
[sccp-driver]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SCCP.cpp#L54-L121
[early-cse]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/EarlyCSE.cpp#L513-L628
[gvn]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/GVN.cpp#L112-L143
[newgvn]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/NewGVN.cpp#L248-L613

[mem2reg-promotable]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/PromoteMemoryToRegister.cpp#L66-L104
[sccp-edge]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SCCPSolver.cpp#L1304-L1320
[sccp-phi]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SCCPSolver.cpp#L1450-L1505
[sccp-work]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SCCPSolver.cpp#L2260-L2286
[sccp-binary]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SCCPSolver.cpp#L1761-L1830
[early-simple]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/EarlyCSE.cpp#L84-L130
[early-elim]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/EarlyCSE.cpp#L1428-L1457
[gvn-expression]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/GVN.cpp#L596-L632
[gvn-leader]: https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/GVN.cpp#L3401-L3426
