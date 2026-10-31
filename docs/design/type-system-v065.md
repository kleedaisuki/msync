# v0.6.5 inferred types and operator capabilities

## Shared implementation contract

Source remains declaration-free: the compiler infers semantic types from values,
operator capabilities and specialized lexical functions. The semantic model names
`Path`, `String`, `Scope`, `Bool`, `Range<T>`, process spaces and ports explicitly.
The persisted 0.6 control representation's `Space` spelling remains a compatible
wire name for the semantic `Scope` type; this is not a second source type.

| Operator/capability | Accepted operands | Result |
| --- | --- | --- |
| `/` | `(String | Path) × (String | Path)` | `Path` |
| `::` | `Scope × (Path | String)` | `Path` |
| `::` | `Scope × Scope` | `Scope` |
| program member selection | `Process × member name` | typed port |
| `and`, `or`, `not` | `Bool` operands | `Bool` |
| `==`, `!=` | matching `Bool` or matching `String` | `Bool` |
| `<`, `<=`, `>`, `>=` | `String × String` | `Bool` |
| range pipe | `Range<T> × built-in view API` | adaptor-dependent |

Operator signatures are centralized compiler capabilities, not scattered spelling
patterns. Endpoint conflicts, rooted right-hand joins and namespace-member rules
remain value constraints checked after the operand types are accepted. Function
calls are normal expressions: qualification applies to their typed return values.
In particular `$wsl::codex(config.toml)` applies the WSL scope to the path returned
by the ordinary lexical `codex` specialization; no special-case rewrite of that
function's spelling is permitted. Unbound paths retain deferred endpoint/home
resolution until qualification supplies their scope.

## AST and scheduling

- `ExprKind::Range(Vec<Expr>)` represents `[item, ...]`; each item uses the normal
  expression parser and its own original source span.
- Existing `Select`, `Join` and `Call` nodes represent nested scope composition
  and qualified function results. Parser code does not infer their types.
- `if release CONDITION:`, `if acquire CONDITION:` and `if acqrel CONDITION:`
  normalize to the existing `Scheduled` wrapper around an `If` statement. The
  flags are release `(true,false)`, acquire `(false,true)`, acqrel `(true,true)`.
  Conditions remain strictly boolean. Scheduling uses existing compiler rules,
  not a new global-join implementation.

## Ordered ranges and built-in APIs

`$view::NAME` is the canonical built-in API namespace, including view adaptors
and scalar consumers. Existing `views::`/`ranges::` spellings remain compatibility
aliases where they already worked. Bare user functions named `filter`, `single`,
etc. retain ordinary lexical meaning outside the established pipe position.

Literal ranges preserve insertion order and may contain only `String`, `Bool`
and scoped/default-local `Path` values after ordinary symbol/boolean expansion.
A heterogeneous permitted range has an inferred scalar union item type; it is not
silently rejected merely for mixing permitted members. Operations on an item
must be supported for every possible member type. A callback that ignores its
argument can therefore transform a mixed range to one scalar type, while a bool
condition on an unresolved `String | Bool` item is a static error. Nested ranges,
scopes and ports are not scalar range elements. Empty ranges remain representable
without filesystem enumeration.

The HIR extends its scalar type representation with a canonical finite union;
old single-type paths/strings and old typed-control objects keep their wire
representation. Consumers, callbacks and object validation share the inferred
item type. Runtime receives typed expressions and tagged values, never source or
frontend AST for recompilation.

## Ownership and acceptance

The language lead owns semantic operator/type and scope composition integration.
Parser and range workers own bounded AST and range/HIR/runtime changes respectively.
Toolchain owns lexical/editor presentation and follows the AST contract above.
Independent public acceptance belongs in `tests/acceptance_v065.rs`.

Local acceptance must include ordered Bool and mixed literal lists, lexical
callbacks, inferred operator rejection, the supplied qualified-symbol example,
source-free object/IR execution, and existing startup recovery contracts.
Compiler performance is measured separately from runtime filesystem/network work.

## Scope composition and member-dependent capabilities

Scope composition applies the inner scope first, then the outer scope. It retains
both captured contexts for runtime-produced scopes; it does not silently rebind
a previously contextualized path. A conflicting explicit physical endpoint is a
value error after type admission. In particular local `$pwd`/`$file` projections
are not automatically reinterpreted as remote filesystem paths. An unqualified
HOME/path returned by a normal symbol remains unbound until the outer endpoint
qualifies it, which is why the supplied WSL example works without local-cwd or
local-HOME substitution.

Program members are deliberately value-dependent: `stdin` is writable while
`stdout`, `stderr`, `pid`, `exit_code` and `meta` have their established readable
port identities. The generic Scope selection relation does not manufacture a
common ReadPort result for a Process; the known-member resolver refines its
actual capability. Built-in API member application is similarly resolved through
the typed API scope and the API's signature. Aliases of `$view` are actual Scope
values, not extra namespace-name patterns.

## Dependent pipe capability

The pipe operator has a dependent capability rather than a fixed binary result:
`Range<T> × ViewApi<T, R> -> R`. Its implementation is
`compiler::structured::ranges::{resolve_pipe, apply, callback}`. `resolve_pipe`
first synthesizes the left value and requires an actual typed Range. The right
member is resolved through the typed API Scope (or the established compatibility
spelling), then `apply` specializes its signature using `RangeExpr::item_type`.
Recognizing a member name is not sufficient to admit the operation.

| API capability | Additional checked input | Statically determined result |
| --- | --- | --- |
| Identity, slicing, reverse, unique | Arity; constant count/stride constraints where required | `Range<T>` |
| Filter, take/drop while | Callback specialized as `T -> Bool` | `Range<T>` |
| Transform | Callback specialized as `T -> U`; `U` is a permitted scalar or finite scalar union | `Range<U>` |
| Empty | Arity | `Bool` |
| Any/all/none | Callback specialized as `T -> Bool` | `Bool` |
| Front/back | Arity | `T` |
| Find-if | Callback specialized as `T -> Bool` | `T` |
| Contains / find-value | Search type accepted by `T` | `Bool` / `T` |

`callback` installs a synthetic slot with exactly the inferred item type, then
uses the ordinary lexical function specialization and operator-capability
checks. A Bool|String item therefore cannot acquire Bool-only operations merely
because it is passed through a filter callback. The resulting typed RangeStage
or RangeQuery carries its scalar result type; untrusted object validation checks
the callback/result relation again. Runtime executes that typed plan, not an API
name or source fragment. There is consequently no second, weaker pipe type
system or need to add a misleading fixed-result Pipe arm to `OperatorTrait`.

Existing focused evidence exercises all three result families: the Bool-list
front/condition journey, mixed filter/transform/contains source-free object/IR
journey, and mixed Bool-only callback rejection in `tests/ranges_v065.rs`, plus
the captured-context mixed-path transform in `tests/types_v065.rs`. This audit
adds documentation of those existing checks, not a redundant implementation or
a repeated test run.

## Compact representation and negotiation

The executable scalar universe for literal range elements has only Bool, String
and Path. Its four multi-member unions use a compact `ScalarUnion` enum rather
than a Vec carried by every scalar expression; `Type` occupies at most two bytes.
Construction joins the finite members without per-item Type allocations. Serde
converts the compact payload to/from the readable canonical union array, rejects
malformed/noncanonical unions, and preserves previous scalar wire spellings.
Semantic operator checking distributes over every possible member without
allocating intermediate result vectors.

The negotiated object feature is `typed_values_v2`. It is required for Bool/union
range values, the atomic implicit RangeTransfer operation, composed/API Scope
values, Scope-valued selection and the `IntoPath` conversion. Existing source
syntax that lowers entirely to old semantic primitives does not acquire an
unnecessary new wire format. Previous 0.5/0.6 objects remain accepted.

`IntoPath` is an explicit typed-IR conversion only where a Path|String union must
become a path: actual Path values keep their endpoint and mode, while actual
String values use the captured local cwd. It avoids implementing a mixed union's
`/` operation by forcing every possible path through an explicit Local scope.
Its input is still a typed expression, never source text.

## Engineering and research basis

The [Rust operator-expression reference](https://doc.rust-lang.org/reference/expressions/operator-expr.html)
separates operator syntax from the traits that define accepted operands/results.
The implementation follows that capability-table principle, without exposing a
new user trait-declaration language or dynamic trait-object dispatch.
[Dunfield and Krishnaswami's bidirectional typing survey](https://research.cs.queensu.ca/home/jana/papers/bidir-survey/)
provides the useful synthesis/checking split: values and specialized lexical
function bodies synthesize types, while operators, conditions and callbacks
check the required capabilities at their source sites. This finite first-order
DSL does not import a higher-rank inference engine or require user annotations.
Existing persistent maps, lexical capture, serde and iterator primitives remain
the underlying mature implementation mechanisms.

## Single synthesis and focused evidence

A computed namespace is synthesized once, then dispatched by its resulting Scope
value. The compiler no longer evaluates a computed namespace speculatively to
ask whether it is the built-in API and then evaluates it again for ordinary
selection. That duplicated fallback can recursively multiply specialization in
nested Scope-returning calls. A 20-level computed Scope chain and a function that
returns the API Scope are concrete regression cases; this is a mechanism fix,
not a claimed benchmark ratio or a new cache.

Direct local Cargo checks passed for the two `types::tests` capability cases and
all six `tests/types_v065.rs` journeys. The latter cover mixed Path|String joins
against a captured context distinct from the test process cwd, explicit feature
negotiation, Bool source types erased by later String projection, retaining a
returned remote endpoint under File selection, bounded nested Scope synthesis,
and typed API dispatch through an ordinary function result. Selected strict
Clippy passed for the library and the new type/parser targets plus the affected
0.5.2 parser helper. Parser/range/CLI acceptance owners retain their larger
focused corpora in the corresponding grammar/range/validation notes; these checks
are not a full-project acceptance claim.

Implicit finite range transfers now lower to the explicit RangeTransfer semantic
operation and have one statement-local cohort publication. Explicit source `for`
and previously serialized For opcodes retain their established per-iteration
semantics. The loader does not guess hidden source intent from debug spans or
reparse stored debug text. Existing objects containing the old implicit-selector
For lowering require source recompilation to obtain the corrected cohort opcode;
this is an explicit bytecode-compatibility boundary, not a claim that historical
object bytes are retroactively rewritten. Physical streams retain explicit
iteration semantics; a finite atomic cohort does not silently become a sequence
of independently published stream writes.

## Compatibility corrections from the broader local pass

Central operator admission does not replace operand ownership. A rejected Join
operand still uses the established path-fragment diagnostic, repair hint and
expanded binding description at that operand's owned source span, with its
inferred unsupported type retained in the explanation. Reporting only the `/`
operator would lose the caller's actual bad argument and is not an acceptable
consequence of making the type relation explicit.

Regex selection now produces a Range, but invalid-pattern diagnostics use the
existing decoded selector interval rather than the enclosing expression span.
This retains escaped Unicode suffix provenance without interpreting a regex
library's formatted human error. The unchanged `source_v4` quoted-suffix case
passes with its exact decoded-byte range.

The `$pwd` member projection is shared by direct syntax and typed computed member
values. An ordinary function returning the member name `pwd` must select the
working-directory item, not invent a `cwd/pwd` child. This uses the same named
scope primitive as direct selection; it neither reparses source nor looks up an
unrelated caller binding with the same spelling. The unchanged computed-member
and caller-owned-argument cases in `symbol_v052` pass, as do that target's complete
18 focused symbol cases and the affected legacy CLI/REPL and Join cases.

A spaced path operator before `<regex>` retains the same selector atom boundary
as a compact slash. The parser fix preserves regex-internal delimiters and does
not turn ordinary String comparisons into selectors. Relocation remains in the
typed Range's root; tests inspecting the old top-level Transfer slot must inspect
the corresponding typed root rather than conclude the anchor disappeared.
