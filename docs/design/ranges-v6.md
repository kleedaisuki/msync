# Typed lazy ranges in 0.6

## Boundary

A selector is a range-producing expression, not a filesystem observation during
compilation. The compiler validates the regular expression using the existing
`regex` dependency and resolves the directory's typed endpoint identity. Runtime
opens the directory only when a consumer asks for its first item. Range elements
are paths or UTF-8 strings; mapping to booleans is rejected. Boolean algorithms
consume ranges and produce a statically boolean scalar instead.

A pipeline captures a typed expression graph, never source text. User predicates
and projections are instantiated using the ordinary lexical symbol/boolean
machinery with a synthetic typed item variable. This preserves definition-time
`global` capture and avoids adding an independent interpreter or closure scope.

## Prior art

- The [C++ range adaptor object contract](https://eel.is/c++draft/range.adaptor.object)
  defines `R | C` as application of the adaptor closure `C` to the range `R`.
- [C++ filter views](https://eel.is/c++draft/range.filter) retain a predicate and
  seek satisfying elements only through iterator access. We use the same
  separation between constructing a view and consuming its elements.
- [Rust `std::iter`](https://doc.rust-lang.org/std/iter/) supplies mature lazy
  iterator combinators. Runtime adapters should reuse iterator state machines
  where practical instead of building eager intermediate vectors per stage.

Ordering is deterministic directory-entry name order, matching the established
selector contract. This may require buffering directory names; it does not permit
eager predicate evaluation, opening every file, or running the pipeline before a
consumer. Consumers such as `any_of` must short-circuit, and an upstream `take(0)`
must not ask its input for an element.

## Supported scalar-element operations

| Family | Operations |
| --- | --- |
| Sources | `<regex>` selection; `ranges::of(...)`; `views::single(value)` |
| Lazy views | `filter`, `transform`, `take`, `drop`, `take_while`, `drop_while`, `stride` (`step` alias), `unique` (adjacent equal elements), `reverse` |
| Identity views | `all`, `common`, `as_const`, `as_rvalue` |
| Boolean consumers | `empty`, `contains(value)`, `any_of(predicate)`, `all_of(predicate)`, `none_of(predicate)` |
| Element consumers | `front` (`first`), `back` (`last`), `find(value)`, `find_if(predicate)` |

Views and consumers accept pipeline application (`items | views::filter(p)`)
and direct namespace-qualified application (`views::filter(items, p)`). A
zero-argument adaptor can omit parentheses in a pipeline. Bare adaptor names are
accepted only on the right of a pipeline, so normal user functions named
`filter` or `single` remain lexical functions elsewhere.

`take`, `drop` and `stride` counts are compile-time decimal constants, expressed
as bare decimal tokens or quoted strings (including string bindings). `stride`
must be positive; zero is valid for `take` and `drop`. No integer value type is
introduced as an accidental consequence of range slicing.

`ranges::of` requires homogeneous path or string elements; an empty source uses
path as its element type. Transform callbacks may change the element type only
to path or string. Predicate callbacks must return bool. Search values must have
the exact element type; paths are not silently converted to filename strings.

Tuple/subrange-producing C++ adaptors (`zip`, `enumerate`, `chunk`, `slide`,
`split`, `adjacent`) are not representable in the requested path/string-only
element domain and are not exposed as misleading partial implementations.
`unique` removes adjacent duplicates, not all repeated elements globally.

## Focused acceptance evidence

`cargo test --locked --test ranges_v6 -- --nocapture` passed all six cases on
2026-10-31. They cover static compilation of a missing directory, predicate and
projection specialization, finite sources and direct applications, stage/query
types, and diagnostics for wrong element/search/result types, counts and arity.
Runtime laziness and file effects are exercised separately by the public CLI
journeys in `tests/cli_v060.rs`; compiler-only success is not evidence of runtime
short-circuit execution.
