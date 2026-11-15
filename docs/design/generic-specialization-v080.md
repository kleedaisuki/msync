# Higher-order specialization and recursive inference

Generic functions specialize over semantic argument and capture information,
not only runtime `TypeKind` values. A `Function` argument includes its concrete
callable identity; a `Scope` argument includes member schemas and their callable
identities. Associated Type values and Range element information also affect
the specialization. Scalar literal values do not specialize ordinary generic
functions.

## Generated standard-module captures are reads

Source expressions such as `$std::containers::List::size(self)` capture a
compiler-owned module location named `$std::containers`; a materialized root
instead captures `$std`. These generated names are absent from the source-name
walk, which sees `std`. They must nevertheless contribute their complete
semantic capture information to the specialization key. Treating them as
write-only locations erases member/callable changes after a whole module or
family replacement and can invoke the previous body with a new closure's
capture layout.

There is no fixed numeric-module shortcut in the key. Standard-module captures
use the same ordinary Scope/member/callable projection as user namespaces. A
stable module location is not proof that its members remain unchanged, including
when a function returns another closure that reads the module later.

The generated dependency representation establishes these as reads. An ordinary
write-only location capture contributes its storage schema, not the old value or
callable graph. Conditional writes are represented by the function's formal
write plan; a no-write branch preserves the current caller's value when the plan
is applied. It does not require another body for each incoming callable.

## Share formal plans before constructing bodies

The specialization key is computed before reserving a function or analyzing its
body. It contains typed input roles and relevant callable, Type, layout and alias
dependencies, not concrete caller region/location identities. By-value input
roots and Range element values do not inherit their source variable's storage
constraint. Scope rows do not carry unrelated reaching-definition alternatives.
An incomplete cache entry is the recursive promise for that same key; there is
no separate active-recursion identity or late whole-module body-folding pass.
Different callbacks still produce different keys, including nested generic calls
that share a template but do not form a recursive edge.

One cached instance owns the typed formal result, reachable formal object/cell
metadata, contracts and a branching write-event program. Call sites bind formal
roles to actual arguments and capture locations. They do not retain an independent
callee heap/effect snapshot. Input alias partitions are premises only when the
analysis uses them; introduced alias relations are transferred back to the
caller, not replayed as fresh allocations for borrowed input objects.

Only writes reachable from formal inputs, captures or returned values are
exported. Reachability expands through values installed by those writes; this
also retains an escaping closure's cells. Private local writes remain useful
during function/loop analysis but do not root the entire activation in the cached
plan. Empty alternatives are preserved when another alternative writes, because
"leave the caller unchanged" is a real branch, not a missing summary.

Every control-flow exit carries its value and write-event sequence together.
Function plans join normal completion and explicit returns; loops join their
break exits. Omitting an early-return branch can incorrectly remove Null from a
mutable linked field's inferred domain. The B+ tree churn journey exercises this
through page split/merge, complete deletion and reinsertion. The Range journey
also checks shared write-only setters with no-write, return and break branches.

Branch contexts borrow unchanged facts through overlays. Joins read every
branch's value for a key before publishing that key to the common ancestor;
otherwise the first branch can contaminate an unchanged sibling. This is
transient control-flow state, not a retained summary per invocation.

## Returned values and deferred reads

A function returns a value, not an alias of a private parameter or local binding
cell. The compiler detaches that top-level storage provenance at the return
boundary while retaining the actual value type. An explicitly nullable local
whose selected value is Number therefore does not turn a returned Number into
`Number?`. Scope instance identity, captured cells and indexed-memory provenance
remain intact; this is not a deep copy or blanket removal of storage metadata.
External captured locations retain their callable result contracts.

Explicit traversal callbacks have a different observation boundary. Free reads
inside a callback use the legal future domain of mutable captured locations,
rather than a refinement that held only when the adapter was constructed. The
callback body then analyzes its own control-flow edges normally. This does not
apply to ordinary calls returning Range or to literal element initializers:
those execute at construction, and their argument reads are not deferred.

An unwritten private binding cell retains its current value even when its
admitted storage type is wider. A lexical write analysis identifies declarations,
not name strings: parameters, local patterns, loop iterations and match captures
each own a location. Nested closures and `global` imports resolve the nearest
definition-time location; a later same-name declaration is a different location.
The analysis visits each syntax node once using linked lexical scopes and local
name dictionaries, rather than rescanning the body for every declaration.

Function bodies are closed lifetimes, and a standalone complete source document
can also prove its unwritten root declarations. Persistent REPL roots are not
closed: later submissions may assign them, so they never obtain this proof from
the current submission alone. Private blocks and function locals inside a REPL
submission remain ordinary closed lexical locations. Loop bindings have the
existing fresh `InitializeLocal` lifecycle per iteration.

Scope fields and standard-module export cells remain externally writable even
when their defining file contains no assignment. They do not receive private
location proofs. A stable local Scope binding does not make its payload fields
immutable, and indexed-memory identity/version checks remain independent.

This is not source-level immutability: an actual assignment or captured setter
keeps the full mutable domain. Function syntax facts use a weak cache containing
only declaration positions, never a first caller's semantic environment. Scalar
deferred reads do not allocate the Scope-widening dictionary. Ordinary arguments evaluate at the call; explicit traversal
callbacks execute on demand. No callback capture is frozen to recover a type proof.

## Focused acceptance

`tests/HigherOrderCalls` exercises eighteen programs through source, serialized
relocatable bytecode and serialized contextual executable bytecode at O0/O2/O3.
The 162 executions cover nested and triple application, Result composition,
recursive fresh-Scope arguments, callable parameters, private return values,
live captured mutations, union dispatch, lexical shadowing and captured
Path/String conversion. Eighteen corresponding editor analyses assert the exact
inferred result type. Twelve checks reject stale deferred refinements involving
live unions, written parameters, captured global writers and mutable Scope
payloads. All 192 focused contracts passed on the isolated managed product.

The standard-library owners separately run actual public Result composition,
Heap drain/map/join and terminal pipelines. These focused compiler checks do not
replace those API journeys or Native AOT acceptance.

The Native wave built from `7b891eacedb0593bfb6a2f927616d6b0ab536f53`
found that the original formal-only proof erased valid non-null refinements for
the private `Map::insert` index and a standalone `parameter_types` result. Linux
image `0b45d226284aa6a8f927bddc9653542925aaf3673c992e82da424c11fd7b7cc2`
and a managed Release build of the same product source both reproduce the two
`E0203` errors; this is a declaration-classification defect, not AOT divergence.
After generalizing the analysis, the actual selected `library-policy` journey
passes six source/object/executable routes, 75 byte observations and six negative
diagnostics. The `type-traits` journey passes three routes with 29 outputs and
three forbidden effects per route, plus four negative diagnostics.

`tests/HigherOrderCalls --lexical-locations` adds 101 focused contracts: 72 staged
executions, nine unsafe private/field capture rejections, twelve persistent REPL
and private-function/editor checks, and eight standalone editor analyses. It
covers declaration shadowing, captured writers, loop/match lifetimes and a real
standard-module field replacement. The previous 192 contracts remain accepted.

## Captured record values and nullable admission

An admitted storage domain is not an additional value produced by a constructor.
For example, `Result::err(error?)` accepts Null, but passing a non-null record
does not make that captured record nullable when the constructor parameter is
never assigned. Later callback-contract checks must retain that distinction.
The parameter-write analysis includes nested closures and lexical shadowing;
a real captured setter therefore keeps the location's full nullable domain.

A Null write also does not erase a captured location's established record schema.
When checking a writable captured record after its current value becomes Null,
the compiler retains the captured declaration's row while widening its value
domain. Mutable member facts still undergo the normal capture widening. A
subsequent different record is checked against the established callable contract
rather than being admitted through a shapeless `Scope?` fallback.

Callable diagnostics render nullable record results as `Scope { ... }?`. Dropping
that suffix previously made an actual nullable/non-null mismatch appear to have
identical expected and actual field lists.

`tests/CallableResultContracts` covers record-valued Result elimination followed
by `get_or`, genuinely writable nullable constructor parameters, and incompatible
rows/nullability. Its 63 checks pass through source, serialized `.mso` and
serialized `.mexe` at O0/O2/O3. The actual command-policy source additionally
checks at all three levels without launching its child processes; actual live
Process execution is a separate ownership/terminal journey.

### Program-owned capture aliases in future callable domains

A bound future-write expression can reconstruct the same function template with
an alias of a named program Scope cell. That alias may still hold a provisional
semantic row; it is not another closure environment. Before joining this
candidate into a live callable domain, match its location captures with the
current same-template captures by canonical declaration identity. For Scope and
standard-module/root declarations owned by the program (not a function template),
reuse the current capture source and its live storage value. Exact same-cell
storage aliases also qualify. Current writes therefore remain visible without
replacing a closure's environment from a root object or counting its fields.

This normalization never merges by-value captures or distinct parameter/factory
cells merely because their function template or structural type is equal. Code
sharing and closure-environment identity remain separate contracts.

## Recursive closure stores and active promises (2026-11-11)

A recursive body can store a closure over a fresh local into an outer location
without reading that location. For example, `walk(n)` saves `fn { snapshot }`
when `n == 3` and then recurses. Its incoming callable domain grows with every
abstract activation if each saved location creates a new specialization. This
was a compiler-time allocation loop, not VM recursion: the O0 reproducer grew
past 1 GB while repeatedly instantiating and freezing closure summaries.

The instance cache now separates two contracts:

* Completed instances use the existing full semantic input key, including
  callable environments and alias topology.
* Active recursive promises use a demand-projected key. A location capture's
  old callable domain is irrelevant when its template never reads that
  declaration. Reads are identified from bound HIR declaration identities,
  including nested closure bodies, not source spellings. Argument shapes,
  actually read captures and nested callable identities remain significant.

The projected key lives only while that body is being analyzed. It must not
become a permanent cache alias: a later call that takes the no-store branch
must preserve its own incoming callable. The regression changes that incoming
callable to a third template between two calls; permanent key projection would
compile a dispatch table missing the actual runtime target.

Two associated provenance fixes are necessary after closing the recursive edge.
Control-flow joins retain declaration mappings for newly projected escaping
locations. Future callable enrichment recognizes an uninstantiated generic
capture recipe already represented by a concrete capture in the current domain;
it does not add an unrelated `Any`-typed phantom capture. Existing concrete
closure identities remain distinct, so saved closures continue to reference their
own activation's cells.

Validation: `tests/HirSemanticTyping --recursive-captures` passes 24 journeys
across O0/O2/O3, independent `.mhir` and bytecode round trips, and persistent
sessions. Cases cover the original recursion, retained earlier snapshots, a
skipped store, nonrecursive conditional writes, the third-template passthrough
counterexample, nested higher-order calls, and a changed read capture.
`tests/HirSessionDispatch --factories-only` passes 15 source-session contracts.
The standalone O0 reproducer compiles to eight functions without unbounded
specialization.

### Specialization lookup reads concrete provenance in place

An instance lookup can encode a concrete semantic input graph without first
projecting it into a formal `SemanticContext`. It assigns canonical ordinals
to actual Scope regions, indexed resources, and location captures. Visiting
a repeated anchor emits a backreference; reference identity of an
intermediate `SemanticValue` is not a graph-cycle test on this path. Stable
indexed carriers still contribute their element and storage constraints, and
Scopes still contribute their observed fields and member contracts.

A detached location capture supplies a fallback value when no live caller
fact is available. A direct key can use that value only when all observed
copies agree, ignoring annotations that cannot affect a scalar key. If
callable alternatives or recursive write-only elision can hide an occurrence
of the cell, the existing formal key handles the ambiguity. Structural
views, resource joins, deferred writes, and by-value environments keep
their normalization. The per-lookup ordinal tables never retain the caller
across invocations. Actual instance creation and call-specific effects still
use their separate formal and caller-projection steps.

### Demand-projected Scope fields and native bisimulation folding

Even with demand-projected recursive promises, higher-order stdlib containers
(such as `_Chain` and B+ tree templates) still fanned out duplicate
specializations across two layers:

1. **Frontend Scope field over-specification (`SemanticAnalyzer.Keys.cs`, `SemanticAnalyzer.FormalInput.cs`)**:
   `TemplateInputUsage` walks a template body and nested `TemplateClosure`s
   (cycle-guarded) to distinguish whole-value uses (`WholeReads`) from member
   projections (`FieldReads`). When a Scope parameter or capture never escapes as
   a whole value, `AppendScopeKey` emits only the fields in `usedFields`,
   tracking already-emitted fields per scope instance in `KeyProvenance`.
   In parallel, `CodeSchema` copies `caller.Definitions` and
   `StandardModuleCapture` falls back to `CanonicalDefinition` so captured
   stdlib modules resolve consistently as compact `M<id>` references rather
   than serialized full-module structural schemas.
2. **Mid-end type canonicalization, local slot alpha-normalization, and function ICF (`NativeHirLowerer.Folding.cs`, `Compiler.cs`)**:
   Distinct `HirType` carrier or provenance histories can lower to identical
   executable register code once frontend static checks complete. At
   `OptimizationLevel > 0` for non-session modules (`!PreserveLocals && ExecutionPlans is null`),
   `FoldIdenticalFunctions` runs both at the end of `NativeHirLowerer.Lower`
   (before optimization) and after `NativeOptimizer.Optimize` (post-inlining/DCE):
   - `CanonicalizeTypes` merges structurally equivalent `NativeCodeType`
     records via partition refinement while preserving distinct
     `NativeCodeLayout` indices so layout-identity and `HasLayout` semantics are
     untouched.
   - `CanonicalizeLocalSlots` keeps capture and parameter ABI slots fixed
     (`[0 .. CaptureCount + ParameterCount - 1]`) and renumbers live
     `Local`/`Temporary` slots in instruction-traversal order, shedding dead
     slots left behind by inlining and DCE.
   - Partition refinement (`StaticShapeKey`, `DynamicKey`, `SplitConflictingDispatchKeys`)
     merges bisimilar non-template functions (ignoring non-session
     `Allocate.B` allocation-site tags) without introducing dispatch-key
     collisions in `FunctionDispatch` or `SessionDispatches`. Execution phases
     emit `compiler.lower.fold_functions` telemetry (`fold.types.*`,
     `fold.slots.*`, `fold.functions.*`, `fold.iterations`) only when
     `CompilationTrace.Detail` is active.
