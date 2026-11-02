# Ordered scalar ranges in v0.6.5

## v0.7.0 Scope range extension

`Range<Scope>` uses the same tagged, lazy range cursor as scalar ranges. Finite
Scope lists, Scope-valued transforms and `front`/`back`/`find` consumers preserve
the actual endpoint and captured context. Merely enumerating an endpoint does
not authenticate or execute remote work. Ordinary lexical specialization gives
loop variables and callbacks the inferred Scope type, so `scope::file` uses the
normal qualification operator rather than endpoint spelling recognition.

Unqualified path items retain their raw spelling in `UnboundPath` typed IR,
alongside the normal relocatable local projection. A range loop must not turn
`~/.codex/config.toml` into an explicitly Local path before `scope::file`
chooses its actual endpoint. Local consumption uses the captured local
projection; remote qualification uses the original spelling so the selected
endpoint owns HOME expansion. Explicit endpoint/contextual paths remain bound
and cannot be silently reinterpreted on another endpoint. Dynamic path joins
preserve this provenance, and source-free objects negotiate the new semantics
through `native_std_v1`.

All existing executable scalar control types are now permitted as range items.
The compact Bool/String/Path union representation remains unchanged. A list
mixing Scope with another scalar domain has no representable union in that
model and is rejected at inference; object validation independently applies the
same join rule. Empty lists retain their existing default Path item type.

Source functions returning a Range retain scalar argument/local snapshots in a
`RangeSource::Let` node, rather than forcing the result through scalar lowering.
Its cursor evaluates those bindings once on first demand, reinstalls their
cached values only while advancing the inner cursor, and restores the enclosing
slot environment afterward, including failed advances. An outer `take(0)` never
initializes the bindings. Object validation checks each binding before defining
its slot, scopes the definitions around the inner range, and context linking
remaps both definitions and uses. This makes source-backed
`$std::ranges::single(fs::filename(input))` ordinary typed composition, including
when its argument is a runtime result. The focused v0.7.0 journey also uses a
directory-creation result twice to observe exactly-once evaluation and verifies
that zero-demand ranges perform neither missing-path queries nor mutations.

This reuses the existing generic runtime rather than introducing a second
endpoint iterator. It follows the useful distinction in Rust's
[Iterator contract](https://doc.rust-lang.org/std/iter/trait.Iterator.html):
adaptors operate on an associated item type, while operations impose only the
capabilities they actually need. Endpoint qualification therefore belongs to
Scope, not to the cursor.

`tests/ranges_v070.rs` exercises the supplied SSH/WSL list without connecting,
then runs local Scope aliases through nested copy macros, identity transforms,
filters, unique, `front` and `contains` from compiled objects and emitted IR after
deleting the source. Earlier Scope-rejection assertions below describe the
v0.6.5 boundary and have been migrated to mixed Scope/scalar rejection.

## Representation and inference

The literal form `[expression, ...]` creates the same lazy `RangeExpr` plan as
selectors and finite built-in sources. Each item goes through ordinary lexical
resolution and symbol/boolean specialization before scalar lowering. Path, String
and Bool are the only item domains; a Scope, port or nested Range is not silently
stringified. Unqualified path values retain the default local/contextual path
semantics; quoted values remain literal string data.

A heterogeneous source infers the canonical finite union of its item types.
`hir::control::Type::join` combines the finite scalar domains, removes duplicates
and orders members; a singleton retains its original scalar wire form. The four
possible multi-member subsets use a compact Copy enum payload, not a Vec carried
by every scalar expression. Normalization allocates no set or second list-sized
vector. The human-readable union-array wire representation remains unchanged via
Serde conversion, which rejects duplicate, nested or out-of-order members. `Type::accepts` defines scalar membership and union
subset compatibility for search operations. Object validation recomputes source
and stage types rather than trusting annotations.

There is no `Any` fallback or runtime parsing. A callback receives the inferred
scalar/union slot type. An operation must be valid for every possible member;
for example a Bool condition over `Bool | String` is a static error. A callback
that ignores its input can transform a mixed list into String or Bool, and an
identity callback can retain the permitted union. Compiler-owned operator
capabilities live in [the common type contract](type-system-v065.md).

The finite-union rule follows the production-language principle documented by
[TypeScript's union types](https://www.typescriptlang.org/docs/handbook/2/everyday-types.html#union-types):
operations require support across all union members. The implementation uses a
small explicit Rust enum, not a TypeScript runtime or a new general inference
engine. [Rust Iterator](https://doc.rust-lang.org/std/iter/trait.Iterator.html)
provides the underlying ordered iteration and short-circuiting model.

## Built-in APIs and laziness

`$view::NAME` is the canonical API scope. A lexical alias such as `api => $view`
retains that API identity; dispatch checks the resolved intrinsic scope rather
than matching the alias's spelling. Established `views::` and `ranges::` forms
remain compatibility aliases, and ordinary functions with adaptor-like names
remain ordinary lexical functions outside the established pipe context.

The runtime cursor borrows the plan and evaluates Values entries in insertion
order, on demand. It already carries tagged Bool/String/Path values, so mixed
ranges do not require another interpreter or eager intermediate conversions.
`take(0)` does not evaluate even the first source item, and query predicates retain
short-circuit behavior. Search accepts a member type of a union and compares the
actual tagged value; a Bool is never confused with a similarly spelled String.

## Statement ownership

An implicit `range -> container/` is one finite publication statement, not sugar
for a user-written `for`. Its `ControlInstruction::RangeTransfer` retains the
range and destination, materializes the demanded finite source cohort, and sends
it to the existing publication owner as one operation. Explicit `for` bodies
still execute their own statements per iteration.

This distinction is required by the unchanged selector collision contract: an
error on a later selected member must not leave an earlier member published.
Empty range transfer must not allocate runtime-private storage, create its
container or wait for a destination it will not touch. Destination flow admission
therefore occurs only after a demanded nonempty cohort exists. The generalized cohort shares ordinary capture, assignment, collision
checks and durable publication; it does not add a database transaction subsystem.
Remote destination integration uses one existing endpoint publication journal,
not a per-item fallback. Remote cohort acceptance is tracked by the publication
owner rather than inferred from local cursor tests.

## Compatibility and local evidence

New Bool/union ranges, RangeTransfer and composed/API scopes negotiate the
`typed_values_v2` object feature. Existing scalar wire names and legacy objects
remain valid; the semantic Scope type retains the established `space` wire name.
Three prior rejection examples in `tests/ranges_v6.rs` intentionally change under
the requested broader item domain: Bool transforms, Bool finite sources and mixed
finite sources are now valid. Those negative tests move to still-unsupported
Scope and nested-Range values, retaining their original type-checking purpose.

The integrated local range slice passed sixteen test functions across incremental windows:

| Command/target | Result and scope |
| --- | --- |
| `cargo test --locked --test ranges_v065` | 7 passed: 64 generated union cases, <=2-byte Type layout and stable/malformed serde, real Bool list order, typed API aliases and mixed callbacks/member search through source-free object/IR, invalid item/callback rejection, real cohort publication retaining its causal instruction parent, and an empty cohort not acquiring a held untouched destination |
| `cargo test --locked --test ranges_v6` | 6 passed after migrating only the three superseded rejection examples; counts, arity, typed predicates, direct API calls and scalar consumers retained |
| `cargo test --locked --lib range_tests` | 3 passed: exact mixed tagged sequence, IntoPath preserving a WSL-tagged Path while contextualizing only a String, composed scopes retaining distinct captured contexts |

The cohort worker restores the current instruction trace context inside its
blocking closure, so publication diagnostics retain the causal submission and
instruction rather than becoming unrelated trace roots. The separate JSONL
journey verifies that ancestry and both published payloads. Selected strict
Clippy passed on the production/range targets and on the final integration-test
target; owned Rust files passed rustfmt and whitespace checks.

The WSL-tagged conversion test is pure interpreter evaluation, not a WSL transport
claim. Remote cohort acceptance remains tracked by the publication owner; these
checks are not a full release matrix. Temporary logs are under `.temp/v065`;
tests and this note retain the reproducible workloads after those logs are removed.
