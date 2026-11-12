# Typed semantic HIR

`Msync.Hir` depends only on `Msync.Core`. Its immutable `HirModule` is a
source-independent semantic program, not an instruction wrapper or syntax tree.
Sources own diagnostic ranges; declarations own logical location identities;
types describe fixed storage, structural rows, function signatures and symbolic
generic relationships. Structured nodes retain blocks, branches, matches, loops,
values, locations and capture modes. Calls already select native identities,
specialized functions or a complete dispatch domain.

Finalized function entry value types are distinct from mutable location
storage. For example, a specialization may receive one exact Scope layout
under a structural parameter constraint, then rebind that parameter to a
different satisfying layout. The HIR artifact preserves both facts so the
portable ABI need not rediscover or re-admit the incoming layout.
An aliased mutable Cell uses the structural storage contract rather than its
current value's concrete layout. An actual dependent read can establish a
finite layout at its selected admission boundary.

`HirBuilder` owns mutable construction arenas. `Freeze` publishes immutable
arrays and transfers no mutable compiler frames, AST objects, credentials or
runtime Scope values. Generic templates use the same node/type representation;
only their uninstantiated bodies may contain symbolic types and `TypeOf`.
Finalized callable bodies are closed. An unused generic value has an explicit
`TemplateClosure` identity rather than a fabricated specialized function.

## Lowering boundary

`HirLowerer.Lower(HirModule, HirLoweringOptions?, HirLoweringSession?)` returns
`HirLoweringResult`, containing bytecode and the explicit logical-to-physical
`HirLoweringLayout`. The lowerer does not bind names, infer types, execute
expressions or call back into the compiler. It selects local/capture slots,
branch offsets, iterator temporaries, scope allocation sites and constant-table
indices. It consumes resolved conversions and call targets without searching
for additional specializations. The caller chooses whether to optimize the
resulting relocatable bytecode.

`HirLoweringSession` retains layout identities across REPL generations. Captures
and parameters precede local slots; declaration initialization reserves a cell
before constructing recursive closures and stores back through that same cell.
Session roots are mapped from explicit exports rather than guessed from local
slot order. A template identity receives an opaque runtime identity entry; all
actual calls must target a finalized specialization or explicit adaptation.

Scope construction children are declaration nodes plus an optional discarded
final expression. Fields load their existing storage so aliases and captured
locations retain identity. Named constructions share their semantic singleton
key across specializations. Lowering projects concrete HIR construction
domains into the portable layout and field case tables. An exact case
contains a logical ordinal, never a physical host offset; shape-only
structural projections do not invent a construction case. Raw capture
operands carry the declared location type; open template identities do not
impose an executable capture ABI.

The semantic loop graph consumes the already evaluated iteration source's
element layout fact when it is known. The slot that freezes the iterable
must not replace those construction IDs with the speculative structural rows
used for initial flow projection, even when both rows expose the same fields.
Conversely, when a narrow read follows an opaque producer stored in a local,
the read must establish its layout before a typed use. Lowering distinguishes
those producer locations from ordinary values already constructed or admitted.

Named constructors emit a selected readiness edge and publish fields in
initializer order. A dominating closed lexical namespace used only for field
access emits static field places with no Scope handle. Whole-value and
escaping uses retain the original identity. Lazy Range element flags distinguish already read
values from explicit thunk closures. Return and break close exited iterators
and acquire regions while preserving their result value.

The current HIR artifact schema is format 3, including the separate parameter
entry types. Serialization and text printing operate on these public tables. Reading an
export and lowering it does not parse `HirSource.Text`; the source text is
optional diagnostic material. The focused `tests/HirLowering` executable uses
intentionally invalid source text and checks source-free artifact round trips,
control flow, captures, named-self reassignment, generic identities and stable
session layout.
