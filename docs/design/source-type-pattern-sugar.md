# Source proposal: type operators, logical patterns, and closed ranges

Status: implemented for the v0.9.1 source contract. See the
[language contract](language-v080.md), [native model](native-model-v090.md), and
[release notes](../releases/0.9.1.md).

The source/formatter/artifact slice covers field-led anonymous Scope literals,
explicit field shorthand, expression bodies, null operators, inclusive
numeric ranges, named calls, single-binding lazy comprehensions, conformance
expressions, and type algebra for already-resolved operands. Type annotations
use `or`; the expression pipe remains `|`.

The binder shares a decision lowering for compound `let … else`, `match`
and `is`; only `match` runs the coverage solver. Constant field-presence tests
fold away for known Scope layouts. Coverage partitions Bool, IEEE Number
(including NaN), represented Scope layouts, and Range length and item cases
on demand. Regex and external Path tests use conservative symbolic predicates.
Named generics, local annotations and unevaluated concept signature queries
have source tests, including paired projections from a runtime choice.

For a finite Scope domain, a partial field-presence match lowers to existing
physical layout tests and refines the successful projection; single-layout
field presence folds at compile time. An eight-arm, guarded Number dispatch
compiled at O2 to 129 disassembled instructions after sharing the failed-arm
continuation on scalar guards without bindings, down from 2,230 in the
initial lowering of the same source. This is emitted code size, not a
throughput benchmark. The mixed-layout and no-replay equal-prefix controls
are in `tests/Msync.EndToEnd/fixtures/source_patterns`.

Different-width alternatives over the same captured Range use one lazily
opened activation-local cursor. It retains only the fixed demanded prefix
in typed frame cells and lowers to the existing Native iterator operations.
In the mixed-prefix control (one item requested by the rejected guarded arm,
then two by the selected arm), the callback is invoked exactly twice. An
inner cursor closes on `continue` while the surrounding iterator stays open.
For structural projections, sharing begins once the projected value has been
captured; it does not memoize separate field reads across effectful guards.

## Types

Use `not`, `and`, and `or` for complement, intersection, and union in type
syntax. From tightest to loosest: postfix `?`, prefix `not`, `and`, `or`.
Parentheses group types. Annotations and concept bounds accept compound types
directly; write compound cast targets as `value as (Path or String)` so that
`value as Bool and enabled` remains an ordinary Boolean expression.

`Path or Null` and `Path?` have the same semantic type ID, null admission, and
storage layout. There is no `Nullable` name. Canonical source rendering uses
`T?` for a nullable payload, grouping a compound payload when needed:
`(Path or String)?`. A union preserves associated types on its alternatives;
an intersection requires both constraints, not just intersecting their
runtime carrier masks. Type complement filters a statically known candidate
domain: for example, `(Path or String or Null) and not Null` becomes
`Path or String`. Do not invent an unconstrained structural complement for
open concepts or add runtime type discovery.

The source spelling of a type union changes; expression `|` remains the
existing first-argument pipe. Migrate shipped stdlib and examples in the same
change. Update the formatter, diagnostics and editor, not just the parser.

## Type queries in concept requirements

```msync
concept WithFallback {
    sample: typename Item,
    fallback: fn() -> decltype(sample),
};
```

`decltype(expr)` may appear wherever a concept requirement expects a type.
Expressions bind to required members, to operation parameters in scope in that
signature, and to the concept's definition-time lexical environment.
`decltype(sample)` denotes the static type of that required member after
substitution. A concept parameter is a type, not an implicit value witness.
Determine the type from the candidate's static member/operation contracts, not
from current field contents or by invoking an operation. A field declared
`Number?` does not become `Number` because the current field is non-null.

Reuse the existing unevaluated expression analysis. Resolve dependent queries
when their input types are available; reject genuinely unresolved or circular
dependencies at the requirement, not at execution. Check each finite type
alternative separately so its derived types stay correlated. A query neither
generates a runtime call nor adds a layout or runtime tag. This follows the
unevaluated operand model in the [C++ requirements draft](https://eel.is/c++draft/expr.prim.req.compound).

## Patterns

Use one logical pattern grammar after `is` and in `match` arms. From tightest
to loosest: `not`, `and`, `or`. In pattern position parentheses group a single
pattern; a comma distinguishes parenthesized destructuring. Examples:

```text
if value is Path or String { ... }
if index is >= 0 and < 256 { ... }
if (value is Path or String) and predicate(value) { ... }

match value {
    Path or String => "text",
    null => "missing",
}
```

The last example is exhaustive for `(Path or String)?`. Start relational
patterns with Number literals as bounds; use Boolean expressions for dynamic
bounds. Group the complete `is` test when mixing it with a Boolean expression
that could also parse as a pattern. Use `P as name` for an explicit binding
inside a logical subpattern; keep the existing standalone match-arm binding.

Evaluate the subject once; alternatives and conjunctions short-circuit in
source order. On success, `and` keeps the intersection, `or` keeps the union;
on failure, the next arm receives the remaining cases. Coverage must account
for partial patterns rather than treating a literal, comparison, or regex as
covering its entire carrier. In particular, both ordered sides of zero do
not cover NaN. Reuse existing HIR branches and finite type cases rather than
adding a generic pattern interpreter, RTTI or caller-alias specialization keys.

[C# patterns](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/patterns)
provide the precedence precedent. [TypeScript narrowing](https://www.typescriptlang.org/docs/handbook/2/narrowing)
provides the exhaustion precedent: a match is exhaustive if no input case remains.

## Closed numeric ranges

`a..b` constructs a lazy `Range<Number>` with unit increments and both
endpoints included. `1..4` yields 1, 2, 3, 4; `4..4` yields 4; `4..1` is empty.
`[a, b]` remains a two-element literal. [Rust](https://doc.rust-lang.org/reference/expressions/range-expr.html)
spells its inclusive range `a..=b`; the proposed msync spelling follows
the request and introduces no second spelling. Existing `ranges::iota` and
`ranges::sequence` retain their exclusive-end contract and remain available for
custom or decreasing steps.

Evaluate both endpoints once, left to right, without enumerating the progression.
Proposed unit-step domain: integral Number endpoints in the closed interval
from -2^53 to +2^53, so every demanded increment is exact. Reuse the existing
primitive numeric sequence descriptor/cursor with an inclusive boundary; do
not compute `end + 1` (it can round to `end`), materialize a literal, or add a
generic iterator bridge. Once the inclusive endpoint is yielded, the cursor is
finished without advancing beyond it.

The operator is non-associative and binds more loosely than arithmetic but
more tightly than comparisons. Keep explicit Paths such as
`./backup..old` literal; an unqualified filename containing `..` needs an
explicit path spelling when it would otherwise parse as a range. Check adjacency
against decimal literals (`1..` versus `1.`, decimal endpoints, and `..` as a
parent path segment), not by guessing which names resolve during binding.

## Accepted companion sugar

1. `value ?? fallback`: evaluate the left once and the right only on Null.
   Preserve existing `value?` (a null test); do not introduce truthiness.
2. `node?::parent?::path`: optional read-only member projections over the
   existing `::` references and layouts. Combine with `??` for defaults;
   defer conditional writes.
3. `pattern if predicate => result`: use an ordinary Bool guard for dynamic
   conditions. A failed guard continues matching; a guard that is not known
   to succeed does not by itself exhaust the pattern's input cases.

## Full pattern binding with let-else

```msync
let { status: 200 or 206, body: String as text } = response else {
    continue;
};
let [Number as first, Number as second, ..] = batch else {
    return;
};
```

`let P = expr else { ... };` consumes the same pattern grammar and compiler
as `match`, not just a null check. Include literals, types, relational tests,
regexes, Boolean pattern combinators, bindings, and nested positional and
Scope member patterns. A bare name at a binding leaf captures; `P as name`
binds the successfully matched value with its refined type. `or` branches
must bind the same names and merge their refined types. `and` combines
distinct bindings; `not` introduces none. Guards remain a separate
match-arm construct.

Evaluate the subject once. Publish bindings only after the whole pattern
succeeds; they are available in the surrounding block, not in `else`.
Every path through `else` must leave the successful continuation, e.g. via
`return`, `continue`, or `break`. Reuse the existing cleanup for exits.
These follow the [Rust let-else](https://doc.rust-lang.org/reference/statements.html#let-statements)
binding rules. A shape mismatch goes to `else`, whereas a failed producer
still reports its error rather than masquerading as a shape mismatch.

Scope patterns resolve field offsets over the subject's finite static layouts;
do not turn an opaque Scope into a dynamic field lookup. For refutable Range
patterns, `[a, b, ..]` observes at most two elements; exact `[a, b]` needs at
most three. Observe the demanded prefix once across alternatives/arms, without
materializing the tail or replaying a producer. Do not change the existing
prefix semantics of ordinary destructuring declarations. Range destructuring
in match arms is a real frontend/lowering extension: the current binder rejects
it, despite parsing that pattern.

## Other accepted work

1. **Expression-bodied lambdas:** `fn(entry) => entry::value ?? ""` is equivalent
   to `fn(entry) { entry::value ?? "" }`, for existing Range callbacks and pipes.
2. **Explicit Scope field shorthand:** `scope { :path, :policy }` means
   `scope { path: path, policy: policy }`. Do not adopt bare field punning:
   a bare name at the end of a Scope can already be its result expression.
3. **Null-coalescing assignment:** `cache::entry ??= build()` addresses the
   destination once and writes only on Null. Lower after read-only `??`;
   the operation does not imply a lock or change alias identity.

## New proposals: type inference and data-flow ergonomics

The current language already specializes unannotated parameters from
arguments, infers results, and infers bare concept arguments. Do not rebrand
these as new features. The missing pieces are a way to name type
relationships without spelling arguments at callers, and to omit *part* of
an annotation.

1. **Named, inferred function type parameters:** `fn tail<T>(items: Range<T>)
   -> Range<T> { $std::ranges::drop(items, 1) };` says the element type is
   preserved. Use the existing concept-bound spelling `<T: Named>`. Infer
   from actual arguments and from an already-known expected result when it
   constrains an unsolved parameter (e.g. `let xs: Range<Path> = empty();`
   for `fn empty<T>() -> Range<T>`). Solve the local call before choosing a
   specialization, preserving correlated alternatives. Do not infer from
   objects at runtime or reopen a solved mutable storage type. See
   [Spine-local Type Inference](https://arxiv.org/abs/1805.10383) for the
   contextual inference precedent, not a mandate for higher-rank inference.
   A follow-on is associated-type projection, such as
   `fn lookup<M: Lookup>(map: M, key: M::Key) -> M::Value?`. Resolve Key
   and Value from the same alternative of M; independently joining the key
   and value domains would admit pairs unsupported by any one implementation.
2. **Partial annotations:** `let input: Range<_> = source;` requires a Range
   while inferring its element type. `_` is a fresh local type hole in type
   position, not a wildcard runtime type, shared variable, or fallback to
   `Any`. Solve from local static evidence; report genuinely undetermined
   holes at the annotation. See the [Rust inferred type](https://doc.rust-lang.org/reference/types/inferred.html).
3. **Contract check without a conversion:** `policy satisfies Named` returns
   the original value and concrete type/layout after the existing structural
   check. It neither performs `as` conversions nor makes a runtime Bool.
   Preserve applicable mutation obligations; no new object or wrapper. See
   [TypeScript satisfies](https://www.typescriptlang.org/docs/handbook/release-notes/typescript-4-9.html#the-satisfies-operator).
4. **Named call arguments:** `ranges::slice(items, start: 10, count: 5)`
   makes same-typed operands self-explaining. Keep source evaluation order,
   then place values in parameter order at compile time. If several callable
   alternatives can reach a call, labels must map consistently on all of
   their exposed signatures. Labels form part of the source API, not the
   native calling convention.
5. **Explicit lazy comprehension:** `[for entry in entries if entry::enabled
   => entry::path]` constructs a Range with today's filter/map semantics.
   Evaluate the producer once when constructing the comprehension; evaluate
   the predicate and projection only on traversal. Keep the existing eager
   element-initialization contract for `[a, b]`; no new yield engine.
   [Python generator expressions](https://peps.python.org/pep-0289/) give
   a useful precedent for the timing boundary.
