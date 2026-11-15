# First-person account: ABI annotations and target-boundary repairs

## My assignments and authorship boundary

I am the participating worker named `abi_annotations`. My first recorded
assignment was to repair the interaction between context-unique LLVM constants,
SSA replacement and the existing Native target's representation metadata. I
was not the author of the earlier frontend design, the original Range lowering,
the original alias-analysis policy or the decision to use the entire optimizer
pipeline. I cannot explain those decisions as my own recollection.

My subsequent bounded assignments included:

- per-Use logical ABI expectations, contextual constant materialization and
  ordinary folded Function aggregate constants;
- zero-offset pointer-use adaptation and constant/canonical GEP target lowering;
- Core instruction equivalence/flag helpers and represented call-attribute
  intersection, plus cloning/value remapping utilities;
- the represented MemorySSAUpdater implementation and focused fixtures;
- ordinary Native Select, Boolean And/Or and i1 min/max lowering, including
  pending-result resource ownership;
- the fresh operand-PHI metadata repair in SimplifyCFG common-code sinking;
- injected CondContext transport and consumption in KnownBits/FP-class queries.

Root coordinated integration, serialized builds and executed the principal
journeys. In particular, the later instruction-only RAUW definition-hint fix
was root's change, which I reviewed and supplied focused checks for. The newer
Function leaf SSA representation, carrier-resolution traversal, machine storage,
SROA integration, runtime Function projection and actual-environment-schema
repairs are not my original implementations. Current partial files have also
been extended by other owners; their present contents must not all be credited
to me.

## Why I made these choices

### Separate a shared LLVM value from a Native use's representation

LLVM null/undef/zero constants are context-unique. Mutating a shared constant's
`LogicalType` for each RAUW target made unrelated consumers overwrite one
another's Native representation. I put a known consumer expectation on the
actual `LlvmUse`, retained definition hints for unique SSA/global definitions,
and selected constant slots by `(constant, expected Native type)`. Cloning,
operand growth and PHI operand movement preserve the expectation.

This was representation selection, not an alias-identity constraint. It did not
make pointer identity depend on a logical type, forbid source GEP folding or
introduce Freeze/cast wrappers to manufacture unique constants. The target still
needs enough information to distinguish its existing address, string, Function
and other carriers when LLVM exposes only opaque pointers/aggregate shapes.

That design initially relied too heavily on newly created SSA definitions
already having a usable hint. Real Select and operand-PHI producers showed the
limit. Root's RAUW handoff preserves an annotated removed definition's hint on
an otherwise unannotated replacement instruction, never on constants. My sink
repair copies the originating operand Use's expectation when a new operand PHI
has no removed definition to inherit from. Neither makes annotations part of
LLVM instruction equality or optimization legality.

### Translate declared layout into target address operations

A zero-offset field GEP can fold to the same underlying pointer. The old target
carrier could nevertheless describe the whole Scope rather than its first
field. I adapted the consuming pointer view using existing AddressOffset/layout
operations, and later handled the builder's ordinary constant byte/array GEPs
with DataLayout offsets and declared layout tables. This was instruction
selection over existing information, not reconstructing HIR or proving heaps.

I deliberately did not cache newly emitted address-view instructions across
arbitrary blocks: without an already-dominating definition, that would be
incorrect. This avoided introducing another analysis, but it also left repeated
view emission as an explicit performance cost, not a free conversion.

### Implement requested source utilities rather than substitute analyses

The MemorySSAUpdater assignment required real incremental move/insert/remove,
PHI repair and CFG-update behavior. I implemented those represented source
operations rather than rebuilding the entire analysis after each edit. The
cloning/equivalence and CondContext assignments likewise served actual pass
consumers. CondContext is a local supplied condition, not a runtime assertion;
ordinary expression recursion retains it, while source PHI-edge queries clear
it. Missing-context-instruction queries still consume the injected condition.

I assumed a faithful, reusable dependency would be preferable to each transform
inventing another private approximation. That does **not** establish that every
analysis or transform should run for every function, nor that its cost is
justified by a passing integration journey.

### Preserve selected-arm and resource behavior for pending Select

Ready Select returns the chosen datum. Pending Select waits for the condition
and then only demands the selected arm. Its dependency ownership also retains
candidate resources until selection, including across a frame exit. Root
explicitly identified that lifetime requirement during review; a closure merely
holding C# datum values would not satisfy the existing NativeWork contract.

Retaining candidate resources is not evaluating both arms, but it can lengthen
their lifetimes. This trade-off needed to be stated as a cost, not described as
though lazy selection had no ownership expense.

## What I got wrong or did not account for sufficiently

1. **I initially leaked target-private type IDs into the source graph.** An
   early canonical-GEP repair assigned inferred target pointer types back to
   LLVM annotations. Lowering the same graph again could then interpret those
   IDs against a different type table. Root called out the ownership error; I
   moved inferred carrier state into the lowering Body/selected Native slots
   and supplied a repeated-lowering property checking unchanged graph metadata.
   This was a real implementation mistake, not a hypothetical safety concern.
2. **The initial metadata boundary was incomplete.** Correct shared-constant
   ownership did not itself cover fresh aggregate Selects or opaque-pointer
   operand PHIs. Root found the callable Select gap in actual CLI execution.
   I later reproduced the capture-location sink gap with the unchanged accepted
   AOT binary: O0 compiled, O2 failed at PointerTyID Select emission. Its trace
   recorded one actual sink operand PHI. Fixing each concrete producer helped,
   but these findings showed that successful component cases were not complete
   coverage of the target representation boundary.
3. **I did not quantify the cumulative target cost of the repairs.** Contextual
   constant slots, address-view temporaries, layout lookup tables and additional
   Native copies all consume compilation work and can enlarge the emitted graph.
   Some are necessary representations; their existence does not make every
   duplicate materialization necessary. I did not isolate their marginal
   elapsed time, allocation volume, frame growth or downstream liveness work.
4. **I did not own an end-to-end cost budget for the source ports.** Incremental
   MemorySSA maintenance avoids a recompute-all substitute, but creating AA,
   dominators, loops and MemorySSA still costs work. Faithful source behavior,
   detailed counters and successful execution establish neither a profitable
   pass schedule nor an acceptable O2 compile-time regression. I should have
   escalated the aggregate work/benefit question earlier instead of allowing
   correctness completion to stand in for performance acceptance.
5. **The tests did not establish negligible overhead.** Root-reported Select,
   pointer, updater and CLI passes supported the tested behavior only. I did not
   independently benchmark these assignments and must not turn property counts
   into a performance argument. My first MemorySSA fixture also omitted module
   ownership, producing a fixture NullReference failure; I corrected the fixture,
   not the production DataLayout contract.
6. **The CondContext handoff remains distinct from execution evidence.** I
   published its coherent 35-file change list and staged focused properties for
   root's serialized integration. My handoff did not include a completed build
   or a real InstCombine Select performance result. It must not be reported as
   independently demonstrated speedup or full optimizer completion.

## Corrections I recommend root make

- Treat the supplied paired regression results as the delivery result, even
  where instruction counts or particular loop arithmetic improved. Do not
  explain the regression away with successful correctness journeys.
- Preserve exact producer/consumer carrier information at the representation
  boundary. Do not widen a known address to a nullable generic carrier merely
  to share an aggregate leaf shape; use the existing exact-address path when
  its already-declared representation applies. The Function projection review
  documents a concrete newer example, but I did not author that leaf design.
- Remove redundant emitted address views/copies using already-selected layout
  and local dominance information where trivial. Do not replace repeated small
  repairs with a global alias/type solver or prohibit ordinary LLVM rewrites.
- Make analysis construction and invalidation ownership explicit in the pass
  driver. Use the recorded hot workload and actual rewrite benefit to remove
  repeated or unproductive work; source fidelity is not a reason for an
  unnecessarily expensive integration schedule.
- Keep shared constants immutable with respect to consumer ABI. Any cleanup
  must preserve per-use expectations and avoid writing target-local IDs back
  into the LLVM graph. Range remains an ordinary builtin object, and none of
  these repairs justifies a special lazy Range operation or heap proof.

This statement adds no product change, build, benchmark or commit. It is my
account of the assignments and corrections recorded in this worker's history,
not a reconstruction of private deliberations or other agents' authorship.
