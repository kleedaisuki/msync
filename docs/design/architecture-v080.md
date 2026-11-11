# 0.8 language and execution architecture

The current 0.8.1 semantic boundary is specified in
[Typed HIR and compiler phase ownership](typed-hir-v081.md). The 0.8.0
organization-only acceptance near the end of this note remains a historical
baseline, not the description of the former emission frontend as current code.
The subsequent [contextual I/O backend](contextual-io-backend.md) defines the
current executable-plan contract, including ephemeral filesystem observations,
format 5, graph optimization and execution-only VM ownership.

## Scope and ownership

The 0.8 migration replaces the indentation-based Rust frontend with a C#
compiler and a Native AOT executable. The retired implementation remains
available through the immutable [v0.7.3 Git history](https://github.com/kleedaisuki/msync/tree/v0.7.3),
not a second implementation in the current checkout. Published tags and asset
bytes are never rewritten.

The root build uses .NET 10. The implementation boundaries are:

| Component | Owns | Must not own |
| --- | --- | --- |
| `Msync.Core` | Semantic types, values, typed bytecode, execution-plan data, artifact codec, instruction effects and block formation | Source parser, optimizer passes, authenticated sessions |
| `Msync.Hir` | Immutable typed semantic programs, source/definition/location identities, HIR persistence/printing and pure bytecode lowering | Source binding, type inference, live session objects or native execution |
| `Msync.Optimization` | Machine-independent passes, function/effect analyses, constant evaluation, contextual IR binding/lowering | Source syntax, filesystem observations or authenticated execution |
| `Msync.Compiler` | Tokens/AST, lexical binding, type inference, refinement, specialization, semantic generations and editor observations | Bytecode emission, physical frame layout or executing filesystem/process effects |
| `Msync.Runtime` | Contextual I/O backend, native operations, VM execution/scheduling and process/transfer lifetime | Source parsing or duplicating portable optimizer passes; VM does not assess plan freshness |
| `Msync.Cli` | Commands, REPL, diagnostics, LSP transport and native process entrypoint | A second implementation of source semantics |

Compilers, interpreters and native registries share semantic types rather than
guessing capabilities from identifier spelling. Standard-library source modules
are compiler inputs, not a second prose-only API.

### Project and source organization

The release preparation separates the optimizer into `Msync.Optimization`
because both compilation and contextual execution consume it, while the artifact
codec and value model must not depend on optimization policy. There is no
compatibility facade in Core. The managed project graph is acyclic:

```text
Msync.Cli          -> Compiler, Hir, Runtime, Optimization
Msync.Compiler     -> Core, Hir, Optimization
Msync.Hir          -> Core
Msync.Runtime      -> Core, Optimization
Msync.Optimization -> Core
Msync.Core         -> no other msync project
```

Compiler and Runtime also reference Core directly. The CLI directly references
Hir for semantic artifacts and Optimization for its IR binding commands. These are build-time and ownership
boundaries; Native AOT still links one self-contained executable rather than
loading managed components at runtime.

| Project | Source entry points and domains |
| --- | --- |
| Core | `Types/`, `Values/`, `Bytecode/`, `Artifacts/`, `Native/`, `Context/`, `Diagnostics/` |
| Hir | `Model/`, `Sealing/`, `Lowering/`, `Serialization/`, `Printing/` |
| Optimization | `Pipeline/`, `Analysis/`, `Flow/`, `Memory/`, `Interprocedural/`, `Evaluation/`, `Contextual/` |
| Compiler | Public compiler/session facades; `Syntax/`, `Semantics/`, `Editor/`, `StandardLibrary/`, `Diagnostics/` |
| Runtime | `Planning/`, `Execution/`, `Scheduling/`, `Transfers/`, `Native/`, `Context/`, `Platform/`, `Text/`, `Generated/` |
| CLI | `Program` routing; `Commands/`, `Terminal/`, `LanguageServer/`, `Artifacts/`, `Configuration/`, `Diagnostics/`, `Telemetry/` |

Classes follow state ownership rather than file-count targets. `SourceBinder`
resolves immutable generic bodies and lexical identities. `SemanticAnalyzer`
owns type/flow facts and callable specialization; query workspaces cannot append
executable output. `SemanticSession` owns pending/accepted source generations,
while `HirLoweringSession` owns their logical-to-physical layout. Pure
`SyntaxTraversal` and lexical write analysis do not own semantic environments;
`StandardSourceCatalog` caches source resources, not caller state. The former
`Compilation` emission partials and bytecode-producing editor probe path are
removed, rather than retained as an alternate frontend.

The session bridge is an integration owner: `SemanticSession` coordinates
analysis, lowering and execution feedback, while its Values/Sites adapters use
`HirLoweringLayout` to relate runtime values to semantic identities. Their
current placement under `Semantics/` does not make those orchestration adapters
part of source binding or type analysis. `SourceBinder` and `SemanticAnalyzer`
do not allocate physical slots or emit bytecode.

Runtime `ExecutionSession`, `SessionImage` and `ResourceIndex` are independent
owners rather than declarations appended to the interpreter. Opcode dispatch,
invocation and lifecycle code are separate interpreter partials; native indexed
storage and text-chunk operations are stateless domain owners. CLI command
options and JSON-RPC framing similarly have explicit owners; the language
server retains analysis and request coordination. No service locator or dynamic
dependency-injection layer is introduced by this organization.

The syntax frontend remains in Compiler: its consumers already need compiler
analysis, so another assembly would not remove a current dependency. Transport
is organized within Runtime rather than split behind new interfaces that would
obscure prompt, context and resource ownership. Public language/native/IR
contracts, operation ordering and algorithm implementations are unchanged.

`BlockBuilder.Form` and `InstructionEffects` live in Core because artifact
loading and execution need bytecode structure and per-operation semantics.
Function-level effect analysis and optimizer dependency caches live in
Optimization. Contextual binding/lowering there transforms IR and captured
context; it does not perform filesystem observations or authentication.

```text
*.msync
   -> tokens / syntax tree
   -> bound generic bodies / semantic analysis / specialized identities
   -> immutable Typed HIR ----> *.mhir
   -> pure bytecode lowering
   -> machine-independent optimization and control/effect blocks (-O level)
   -> *.mso
        -> context binding + selected metadata/control computation
        -> resource graph optimization + executable batch formation
        -> *.mexe
             -> VM dependency scheduling / native execution / resource management
```

The `.mhir` container independently versions the typed semantic program. Its
source text is optional diagnostic material, never syntax to replay. Standalone
lowering requires a self-contained semantic entry; live external session values
are not serialized into the file.

Bytecode artifacts contain operations, types, constants, symbolic context and source
locations, not source to evaluate later. Source locations are diagnostic data.
The current binary contract is format 5, language 0.8.0 (semantic flags 3).
Readers reject earlier C# formats 1/2/3/4; compatibility code and old builtin
alias identities are not retained. Historical versioning discussions describe
previous releases, not a requirement to maintain their readers.
Relocatable artifacts capture no live authentication, filesystem snapshots,
handles or optimizer proofs about a previous execution. Executable artifacts
capture contextual operations and selected observations, not remote credentials
or a claim that external files still exist. VM executes the recorded graph;
it does not compare observations with the environment or repair an obsolete
plan. Unknown-effect continuations are prepared when selected with actual
inputs, not as a staleness fallback. Planner state belongs to each selected
frame/region rather than a shared mutable module.

## Language decisions

* Braces define expression blocks. A trailing expression supplies the value;
  `;` discards a value and an empty/discard-only block evaluates to `null`.
  Whitespace and indentation do not establish syntax structure.
* `let` introduces a mutable lexical location with an immutable inferred or
  declared type; another `let` shadows it with a new identity. Assignments
  update the selected value without widening its type. Scope aliases share
  instance identity and field mutations. `_` discards a value without
  introducing a nullable binding. The November 9 requirement supersedes the
  earlier immutable model; see [mutable state and effects](mutable-state-and-effects-v080.md).
* `Null` is a real type and `null` a value, not `false`. Null tests use `is` /
  `is not`; boolean conditions still require Bool. A nullable binding requires
  written `?`, including Scope fields such as `filename?: null`, and `value?`
  means **is null**, following the user's example.
  Tests refine the same binding in the appropriate control-flow branch.
* `fn` unifies the old macro/symbol/boolean distinction. Specialization is keyed
  by function identity and argument types, not source substitution. A recursive
  specialization reserves its identity before lowering its body: recursion is
  a normal typed call edge, not an infinite demand for inlining. New closures
  retain captured lexical locations; parameter expressions execute once.
  `global` explicitly imports definition-time lexical ancestor locations under
  the same shared-cell contract; it is not process-global name lookup.
* Scope is a lexical member object. Connection identifies a physical endpoint.
  Scope/Process member selection uses `::`; `Connection @ (Path | String)` is
  physical qualification and evaluates left then right. Connection `::` is
  rejected with migration guidance. Named Scope declarations initialize once on
  first execution and share an instance within a program/REPL session; anonymous
  Scopes allocate fresh instances. This is not a process-wide shared registry.
* Paths retain raw text, captured local projection and explicit endpoint
  provenance. Requalifying an unbound `~` uses the selected endpoint's home.
  Lexical path operations do not accidentally turn raw text into local absolute
  strings. Path/String conversion follows the consuming operation; a bare
  String transfer source remains literal bytes, preserving the existing useful
  contract rather than guessing from file existence.
* Match is exhaustive over the inferred input domain. Regex patterns inspect a
  path's native string projection. Type patterns operate on semantic types.
  `decltype(expr)` performs inference only; even native/process/transfer
  expressions in its operand are not executed.
* Number is binary64 with IEEE division, unordered NaN comparisons, signed-zero
  equality, truncating remainder and right-associative exponentiation. Finite
  integral Int64 counts/indices serve lazy Range workloads without Int32 caps.
  Numeric syntax and semantics are shared by static evaluation and execution.
* `as` accepts a statically known target and a closed conversion matrix; only
  Path/String conversion is implicit. A named `concept` composes structural
  member, associated-type and function-signature constraints. Checking occurs
  during specialization and annotated-result checking, not through runtime
  dispatch. Concepts neither add TypeKinds/operator overloads nor prove laws.
  See [the language contract](language-v080.md) for syntax and conversions.
* Removed syntax (`:=`, binding `=>`, indentation declarations, async/await and
  the delete statement keyword) is not reintroduced through frontend aliases.
  Native filesystem deletion remains a callable library operation.

## Effects, scheduling and compatibility

Control-flow edges and effect dependencies are separate concerns. Runtime
admission belongs to selected work: a possible SSH operand in an unselected
branch must not connect, prompt or globally join unrelated operations. Unknown
effects prohibit only transformations needing their independence. Static
optimization must not pre-execute filesystem queries, transfers or programs.

The ordinary-copy contract is unchanged: an owned same-directory temporary file,
atomic replacement, at most one file fsync, and cleanup by the live owner.
Completed members of a failed directory/range copy may remain; report the actual
outcome and permit retry. There is no persistent rollback journal, before-image,
startup scan, parent-wide publication lock or filesystem-wide syncfs.
One finite remote transfer uses one native SSH client/channel for metadata and
bytes, preserving configured aliases, agents and native prompts.

The Rust implementation is a Git-history baseline; its formats are not current
C# execution inputs. Binary headers distinguish format version
and language semantics, and unsupported versions are rejected explicitly.
Native AOT distribution must run without an installed
.NET runtime; `.mexe` is msync executable IR, not an OS-native executable.

Contextual binding captures cwd/home/source and binds constants once, lowers
physical builtins and local transfers, invokes the optimizer once at the chosen
level (default O2), then lowers newly exposed transfers/no-ops. Executable IR
runs without rebinding or repeating this optimization pipeline. Structural
artifact validation occurs at read; source type errors are checked before dead
code elimination. Actual I/O admission owns failures and diagnostics rather
than repeatedly proving permissions or file existence. A weak module-identity
cache may retain immutable per-PC block lookup, never mutable cells, credentials
or filesystem proofs. See [runtime details](runtime-v080.md).

## Engineering and research basis

* [Microsoft Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  makes the distribution self-contained and disallows runtime code generation.
  Use a static opcode interpreter and statically reachable native registry, not
  Reflection.Emit or runtime-loaded compiler assemblies. Runtime-specific
  optimization means contextual lowering and execution choices, not a hidden JIT.
* [Rust's monomorphization collector](https://rustc-dev-guide.rust-lang.org/backend/monomorph.html)
  separates discovering concrete callable instances from code generation.
  Adopt identity-based specialization and retain recursion as a graph edge.
* [MLIR bytecode versioning](https://mlir.llvm.org/docs/BytecodeFormat/)
  distinguishes stable container encoding from evolving operation semantics.
  Apply that distinction to msync's own compact format; a dependency on the
  LLVM/MLIR runtime is not needed for this DSL.
* [Fallin and Bernstein, PLDI 2025](https://doi.org/10.1145/3729259)
  derive whole-program compilation through partial evaluation of an interpreter.
  The relevant lesson is one operational semantics across stages, not a promise
  of adopting their C/C++ compiler technique directly in Native AOT C#.
* [Pečimúth et al., ECOOP 2025](https://doi.org/10.4230/LIPIcs.ECOOP.2025.25)
  reuse optimized IR with verified specialization assumptions and relocation.
  Keep persistent machine-independent operations separate from live observations
  and runtime-owned identities; do not persist the latter as portable proofs.

## Integration acceptance

The key acceptance journey is source -> compile `.mso` without effects -> remove
source -> execute -> bind `.mexe` -> change cwd -> execute with captured context.
Use real filesystem bytes and real child processes. Incremental probes should
cover null refinements, exhaustive pattern/type matching, generic/recursive
closures, scope ownership, finite selectors, ordinary replacement, endpoint
selection and authentication ownership. A native published binary must run those
same journeys; managed-build success alone does not establish Native AOT
compatibility. Cross-platform installation evidence supplements local checks.

Implementation-specific language, runtime, tooling and validation findings belong
in their focused 0.8 notes. This document fixes cross-component decisions, not a
claim that every acceptance journey has already passed.
The active [upgrade delivery matrix](language-upgrade-delivery-v080.md) separates
required contracts, owner evidence, managed integration and native acceptance.

### Organization-only release preparation

The November 10 organization uses the accepted `8f2c78e` language implementation
as its behavior baseline. The independent Optimization project changes managed
ownership and build dependencies, not IR fields or algorithms. Existing external
NuGet versions and content hashes are unchanged; all product and test project
lock graphs were restored as metadata, without executing an indiscriminate test
matrix.

The coherent five-project Release CLI build completed with zero warnings and
errors. Its assembly receipt is `.temp/csharp-organization/snapshot.json`.
Focused integration reused the component owners' affected-contract checks:

* Eighteen compiled objects across O0/O2/O3 are byte-identical to the accepted
  Native baseline, including nullable flow, closure captures, associated types,
  Scope self-reference, unevaluated metadata and indexed storage.
* CLI object/executable bytes, execution output, disassembly JSON, configuration,
  redirected REPL and Unicode JSON-RPC responses match before and after the
  organization.
* Runtime extraction preserves 582 executable/data member token streams after
  owner-name normalization. Real archive production, dependency retirement and
  cross-submission Process ownership remain exercised.
* The shared CLI snapshot passes actual REPL/PTY cwd, completion, lazy output,
  gated Process and Ctrl+C recovery, plus the integrated source/object/executable
  language journey. New-assembly optimizer tests retain 44 dependency-cache and
  206 indexed-memory contracts.

Reports are under `.temp/csharp-organization/` and the component-specific
organization directories. These are local behavior-preservation observations;
the release workflow rebuilds Native packages and the editor host from the final
commit rather than publishing an earlier binary under a reorganized source tag.

## Representation handoff and execution hot path

| Handoff | Owned work | Work not repeated by the next layer |
| --- | --- | --- |
| Source to typed HIR | Bind names, check semantics, seal reachable typed bodies | The seal is arena compaction, not a second source typecheck. |
| HIR file to lowering | Decode and validate the external representation once; lower dense identities and slots | Lowering does not run the HIR validator again. Open session imports remain a separate binding requirement. |
| HIR to relocatable bytecode | Emit instructions and run machine-independent optimization | An embedding that executes or reloads session code uses path-only relocation, not another optimizer pass inside the interpreter. |
| Relocatable to prepared executable | The backend may lower context-specific operations, optimize known facts, lay out forward traces and prepare I/O plans | The first lowering pass does not materialize a block index that the optimizer would discard. The execution image is not replanned or rewritten by the VM. |
| Bytecode file to execution | Decode and check the file's format, references and stack/control-flow contract | Execution/check loading does not materialize the separate inspection block projection. Public writer checks remain at the boundary that can receive independently constructed modules. |
| Bytecode to work | A single PC loop per frame; native dispatch resolves one immutable descriptor; actual resource dependencies stay with the scheduler | No per-instruction block index, per-block async state machine, repeated argument unwrapping or repeated name splitting/signature lookups. |

Executable trace layout runs at the backend, before contextual I/O planning.
It chains forward jumps to uniquely targeted trace starts, keeping implicit fallthrough
inside each segment, and remaps explicit PC targets. It does not interchange
instructions along an executed path. Cyclic functions and already planned
images are left to their existing owners; open session entry PCs are stable.
Removing a jump must not expose a Transfer/Pop pair that would change discarded
transfer scheduling. The regression fixture checks both branch outcomes,
ordered file bytes and actual block/jump reduction after an artifact round trip.
This is separate from the I/O graph's resource-version reordering.

The original execution-only Native AOT Set showed 11 of 25 interrupted stacks
at the former block dispatch entry/body. On the same checked 8,192-key bound
image, two order-balanced pairs went from 4.77/5.19 s to 2.71/2.59 s when the
per-block wrapper was removed. A second same-input comparison of normalized
calls and one selected native descriptor gave approximately 2.21 s. The
combined runtime result does not include any savings from re-layout of this
already bound artifact. It is the cost of an old image on a thinner executor.
[Ertl and Gregg's interpreter measurements](https://jilp.org/vol5/v5paper12.pdf)
motivate measuring dispatch separately from instruction semantics; they are
not evidence of the cause or the size of the msync result.

No audit finding warrants weakening format checks, actual buffer index
semantics, Scope shape compatibility for hinted fields, lexical cell ownership,
or waits on selected in-flight I/O. The remaining collection cost includes
generic calls, field/value materialization and the current singleton Range key
representation, independently identified in the earlier source overlays.

The [cross-layer assumption audit](performance-assumption-audit-v086.md)
records follow-up hypotheses, distributions and proof obligations without
changing runtime or language contracts.
