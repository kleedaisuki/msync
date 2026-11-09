# Closed types, explicit operation dictionaries and algebraic contracts

## Implemented named concepts

The active language upgrade adopts named structural concepts, checked by the
compiler at function specialization and annotated binding initialization. They
are constraints over existing Scope values, not additional runtime types.

```msync
concept Semigroup {
    combine: fn(left: typename Self, right: Self) -> Self,
};
concept Monoid {
    Semigroup,
    identity: Self,
};

fn combine_identity(algebra: Monoid) -> Monoid {
    algebra::combine(algebra::identity, algebra::identity);
    algebra
};

combine_identity(scope {
    identity: 0,
    combine: fn(left, right) { left + right },
});
```

Concept names have lexical definition-time ownership, including closures and
REPL submissions. A concept body contains required fields, required operation
signatures, inline associated type binders (`typename Self`), and previously
declared base concepts. Bind an associated name at its first use, including
`Range<typename Item>` or an operation parameter/result, then reuse the bare
name. The old standalone `type Self;` spelling remains accepted input, but
standard sources use inline binders. Associated types are inferred from fields,
operation annotations and checked operation results; a matching Type-valued
member may provide their carrier explicitly.
A concrete associated Scope identity refines a broad Scope carrier and retains
its recursively checked field schema. Inherited associated names are available
to derived requirements. Operation parameters require an explicit builtin,
concept, or associated type; implementation functions remain generic.

Both argument and return annotations are checked without executing the checked
operations. Nullable concepts use the normal explicit `?` rules. Existing mutable
locations retain their concept contracts: replacing a whole constrained value or
a required function member cannot invalidate the checked signature. Concept
checking does not implicitly convert field representations or introduce runtime
AST evaluation. Only ordinary specialization bytecode reaches artifacts.

The checker diagnoses missing fields, incompatible carrier schemas, operation
arguments/results and unresolved associated types with the concept declaration
as related source context (`E0214`). Concepts express capabilities, not proofs
of associativity, purity, or commutativity; they do not permit new optimization
reorderings. `tests/Concepts` exercises source, serialized relocatable bytecode,
context-bound executable bytecode, mutation, nested Scope carriers and sessions
at O0/O2/O3.

### Source-backed standard concepts

Standard concepts are exported as compile-time declarations, for example
`$std::concepts::Equality`. Qualified references are accepted in parameter,
return and storage annotations, concept bases, unions and `Range<T>` elements.
They use the same structural checker as lexical concepts; an annotation loads
only immutable declaration metadata from the embedded source module, not its
runtime Scope, functions or effects. Source modules may also export ordinary
named Scope dictionaries, preserving their program/session singleton identity.

Concept and value exports are deliberately distinct. A concept is not a field
of a runtime `$std::concepts` object, and replacing a mutable standard value does
not replace a declaration. A lexical `std` binding shadows the builtin reference
in a new type annotation, just as in a value expression. Function signatures
and previously admitted storage contracts retain their definition-time namespace
instead of being reinterpreted under a later caller's `std` binding. Parsed
documents are reusable; metadata caches never retain user binding Cells,
working directories or endpoint observations. Editor definition locations refer
to the same `msync-stdlib` source bytes used by compilation.

## Original investigation and rationale

The November 9, 2026 question is how to express concept/trait-like algebraic
requirements without opening msync's type universe. The following investigation predates the implementation above; its distinction
between structural capabilities and algebraic laws remains authoritative. The related [Number investigation](number-semantics-v080.md) owns
numeric representation and language-surface decisions.

**Recommendation:** keep the source representation as ordinary `scope` values
of operations and ordinary generic `fn` algorithms. Add a small compiler-checked
structural requirement facility, elaborated at specialization time into explicit
dictionary passing. Keep capability checking, mathematical laws and optimization
evidence separate. Do not introduce global instance search, user-defined runtime
type kinds or automatic operator overloading to obtain this functionality.

The closed `TypeKind` set and the set of available algebras are different sets.
One carrier can support many structures: Number addition, Number multiplication,
Number minimum, and application-specific checked operations are different
choices, not competing global instances of one `Number` trait. Scope values also
represent products and application records without introducing a nominal type.

## What already works

The current implementation already has most of the operational substrate:

* `TypeRef` has a closed bitset and a Range element descriptor, in
  `src/Msync.Core/Types.cs`.
* `Info` separately retains actual Scope members, callable templates and
  alternatives, Type-value denotations and storage identities, in
  `src/Msync.Compiler/SemanticModel.cs`.
* Generic calls specialize using actual argument/callable/Scope information;
  `Path | String` annotations restrict kinds without creating an open typeclass
  mechanism. See `Functions.Specialize` and `Typing.ResolveType`.
* Scope/Function values and captured locations are mutable with fixed storage
  constraints. Existing callable and dependent-cast contracts show how a
  compile-time requirement can remain attached to storage across later writes.
* `stdlib/ranges.msync` implements a sequential `fold` in source; `reduce` is
  currently an alias of that fold, not a reordering parallel reduction.
* `stdlib/functional.msync` already represents composition as ordinary closures.

The following is **existing syntax**, not proposed sugar:

```msync
scope TextConcat {
    carrier: String,
    identity: "",
    combine: fn(left: String, right: String) -> String {
        $std::string::concat(left, right)
    },
};

fn fold_with(values: Range, algebra: Scope) {
    $std::ranges::fold(values, algebra::identity, algebra::combine)
};

fold_with(["hello", " ", "Klee"], TextConcat) -> ./result;
```

A product carrier is also expressible with existing Scope values:

```msync
scope TextAll {
    carrier: Scope,
    identity: scope { text: "", accepted: true },
    combine: fn(left: Scope, right: Scope) -> Scope {
        scope {
            text: $std::string::concat(left::text, right::text),
            accepted: left::accepted and right::accepted,
        }
    },
};
```

`fold_with` on two records `{text: "a", accepted: true}` and
`{text: "b", accepted: false}` returns a record containing `"ab"` and `false`.
The closed carrier kind is Scope, while the structural carrier schema contains
`text: String` and `accepted: Bool`. That distinction matters for checking a
closed operation: returning `scope { unrelated: 1 }` is not a valid result merely
because both results have `TypeKind.Scope`.

Local probes under `.temp/algebra-constraints` checked both programs and executed
them with the current managed product at O2; actual files contained `hello Klee`,
`ab` and `no`. This is a source-expressibility check, not a Native AOT performance
claim or a new full-platform acceptance run.

## What is missing: requirements are not ordinary Bool predicates

An operation dictionary can already be used, but a declaration such as
`algebra: Scope` cannot state its required fields or operation signatures. Today
the error is discovered when the particular body is specialized.

This attempted ordinary-function test illustrates the boundary:

```msync
fn closed_under(algebra: Scope, sample) {
    decltype(algebra::combine(sample, sample)) is decltype(sample)
};
closed_under(scope { identity: "" }, "x")
```

It reports `E0205` for missing `combine`; it does not produce `false`.
`decltype` does not execute effects, but it still typechecks its operand.
Furthermore, its public Type value says `Function` rather than a full function
signature, and `Scope` rather than a complete row schema. A general concept
checker therefore needs compiler participation; it cannot be implemented as an
ordinary library function that catches arbitrary type errors.

The useful addition is a **requirements kernel**, not an open type system:

1. Match an existing value's structural members.
2. Relate operation parameter/result schemas, including associated `Self`.
3. Check a generic operation for the requested argument schema without running
   its effects.
4. State an effect/stability requirement where an algorithm needs one.
5. Produce a precise diagnostic or a checked binding; a separate non-diagnostic
   `models` query may return false for an unsatisfied requirement.

Checking a signature is stronger than testing one example input: it quantifies
over the requested argument domain. If an operation takes `(Self, Self)` but
returns only String for one selected literal, evaluating that example is not a
proof of closure over Self. Checking must use the compiler's nonexecuting typed
specialization, forgetting literal-specific flow facts where appropriate.

## A precise model

An algebraic contract should distinguish the tuple

```text
(carrier schema A, value domain D, equivalence ≈,
 operation signatures Σ, allowed effects E, laws L)
```

* **A** is an existing semantic carrier/schema. It may be Number, Range<String>,
  or a structural Scope row; it is not a new source-level nominal type.
* **D** is a subset of values of A, possibly described by a membership operation.
  A finite-number predicate alone is not closed under floating-point addition:
  a finite sum can overflow to infinity.
* **≈** is the relation under which the algebra's laws are claimed. Source
  numeric comparison, bit representation identity and a quotient relation are
  not interchangeable.
* **Σ** says what the operations accept and return, including required Scope
  fields, nullable admission, Range element schemas and callable relationships.
* **E** describes observable mutation, IO, enumeration, possible failure and
  divergence. Fresh result allocation is different from mutating an input alias.
* **L** contains universally quantified equations and closure requirements.

A monoid over `(D, ≈)` needs `e ∈ D`, an operation `⋆ : D × D → D`, equivalence
and congruence, and, for every `a,b,c ∈ D`:

```text
(a ⋆ b) ⋆ c ≈ a ⋆ (b ⋆ c)
e ⋆ a ≈ a
a ⋆ e ≈ a
```

A bare binary Function signature provides none of those universal equations.
A membership predicate provides neither closure nor associativity. The same
signature can express subtraction and addition; their algebraic properties
differ.

Conversely, a sequential fold requires only a step operation
`State × Item → State` and an initial State. It need not even have `State = Item`
or be associative. This is why `FoldStep`, `Semigroup` and `Monoid` should not be
collapsed into a universal `Numeric` capability. A future tree reduction may
require associativity; reordering input arbitrarily additionally needs
commutativity or an API contract explicitly permitting that reordering.

Useful initial requirement families are:

| Family | Mechanically checkable shape | Additional meaning |
| --- | --- | --- |
| FoldStep | `State × Item → State` | Ordered evaluation; no algebra law needed |
| Equivalence | `A × A → Bool` | Reflexive, symmetric, transitive |
| Ordering | `A × A → Bool` or a chosen comparison result | Strict weak/total order, with a stated equality policy |
| Semigroup | `A × A → A` | Closure and associativity under ≈ |
| Monoid | Semigroup plus identity of A | Left/right identity |
| Semiring | Two operations and two identities over A | Both monoids as appropriate, distribution, annihilation |
| Sequence producer/consumer | Existing Range element/callback schemas | Demand, replay, early termination and effect contract |

## Three designs and their costs

| Design | Benefit | Problem / decision |
| --- | --- | --- |
| Ordinary Scope dictionary only | Works now; explicit, locally chosen operations; normal specialization/inlining | Errors are use-driven; no reusable declaration of associated signatures/laws. Use as the base representation |
| Compiler-checked structural requirement over a Scope | Early diagnostics and explicit Self relations; finite type universe retained; no global search | Requires a small immutable requirement representation and nonexecuting signature matching. Recommended extension |
| Global trait instances and overloaded operators | Familiar implicit selection | Same carrier has multiple valid algebras; adds coherence, overlap, scope and instance-search machinery. Not recommended for the stated goal |

An optional `requires` clause can later be sugar for the checked-dictionary
form. It should not be the initial semantic primitive. In particular the word
`concept` should not imply that the compiler proves arbitrary user equations.

### Proposed surface sketch — NOT current syntax/API

The spelling below is illustrative. New `$std::contracts` builders would be
compiler-known primitives returning ordinary Scope-shaped descriptions with
compile-time requirement metadata. They are **not implemented** today.

```msync
let Monoid = $std::contracts::define(scope {
    identity: $std::contracts::associated("Self"),
    combine: $std::contracts::signature(["Self", "Self"], "Self"),
});

fn fold_with(values: Range, algebra: Scope) {
    $std::contracts::require(algebra, Monoid);
    $std::ranges::fold(values, algebra::identity, algebra::combine)
};
```

The strings above name requirement-local schema variables, not runtime type
names or new TypeKind values. Self is inferred from the actual identity's
semantic schema. Signature descriptions must retain ordered argument schemas;
the current homogeneous Range item descriptor alone is insufficient for a
heterogeneous list of argument types. That is internal requirement metadata,
not a reason to introduce a general Array type.

`require` should be a nonexecuting declaration-like check at specialization,
with no ordinary runtime call after elaboration. It should report a missing
member, wrong arity, incompatible result/row, Null mismatch or impermissible
effect at the requirement and the actual supplied definition. Matching extra
fields is allowed. Generic operation implementations remain generic; checking
one requested carrier must not impose the same carrier on every specialization.

The name Monoid here only establishes its capability shape. Laws would be
separate named contracts/tests/evidence rather than an `associative: true` flag
that enables optimizer transformations.

## Mutable dictionaries, captures and witness lifetime

The project's most consequential difference from a textbook pure typeclass is
that Scope fields and captured cells are mutable.

Structural evidence can attach to the relevant storage using the same pattern
as existing callable/cast contracts: future compatible operation replacements
remain allowed, while changing arity, result schema or required effects is
diagnosed. Unrelated fields and unrelated objects must not be frozen globally.

Law evidence is stricter. Replacing `combine` by a function of the same signature
can change associativity, ordering or side effects. Capturing a Function value
at algorithm entry freezes its identity, not the contents of its captured cells.
A Range source callback can mutate those cells during enumeration.

Therefore:

* Ordinary `fold_with` keeps sequential existing semantics and needs no law
  evidence. It must not silently snapshot/deep-copy the user's captured state.
* A stronger algorithm can require a nonmutating/stable operation; use actual
  field/capture MOD/REF summaries, not only a coarse Function kind.
* Trusted optimized plans must identify the operation definitions and relevant
  environment assumptions. An assumption invalidation must be rejected or fall
  back according to an explicit contract, not keep a stale optimized result.
* No per-call global instance registry or universal mutation-version counter is
  needed for the initial design. Most generic algorithms simply take their
  dictionaries explicitly; known dictionaries specialize and inline normally.
* A persistent ordered collection must retain its chosen ordering witness.
  Passing a new valid-but-different ordering to operations on the old collection
  is not safe merely because both dictionaries satisfy an Order signature.

The existing memory/effect infrastructure can enforce useful stability subsets,
but it is not a proof of arbitrary algebraic equations. IO or stateful operations
can still be represented and used sequentially; an operation need not be banned
from all dictionaries because it is unsuitable for a pure/reordering algorithm.

## Number, Path and observable equality

Current Number is binary64. This program uses existing integer-spelled literals:

```msync
let a = 10000000000000000;
let b = - 10000000000000000;
let c = 1;
let left = (a + b) + c;
let right = a + (b + c);
```

Actual O0 execution reports `left != right` (1 versus 0). Even finite inputs do
not make binary64 addition associative. Addition is therefore not a general
Number monoid, much less a Number field or semiring under the intended exact
laws. The native math functions being callable does not change that result.

Current language comparison uses `double.CompareTo`, so NaNs compare equal to
NaNs and below ordinary numbers. That existing semantic equivalence differs
from IEEE operator equality and from `ValueRepresentation` bit identity. The
probe also confirms `signbit(- 0)` is true but `signbit((- 0) + 0)` is false:
addition identity depends on the equivalence chosen.

A total-order minimum can have useful quotient-algebra laws under the language
comparison relation, but those laws do not permit changing an existing program's
returned signed-zero representation when `signbit` can observe it. Mathematical
reasoning under a user equivalence is not automatically contextual equivalence
for every language operation. Existing `fold` must preserve its documented order.

For an exact small example without a new type, canonical Numbers 0..255 with
modular addition form a cyclic monoid/group: sums are at most 510 and exactly
representable. The domain must select canonical +0 or use a stated equivalence;
just `0 <= value < 256` also admits -0. This is a good example of a refined value
domain over an existing closed kind, not a new nominal byte type.

Likewise Path join is not automatically a monoid over all Paths: endpoints,
rooted operands, source-relative context and normalization matter. Define the
intended segment domain and endpoint context first. Process/IO sequencing must
also account for cancellation, errors and task-owner joins; the mathematical
associativity of pure function composition does not by itself prove that all
effectful wrapper groupings preserve this runtime's observable scheduling.

## Laws, tests and optimization authority

Keep three statuses distinct:

1. **Capability-checked:** all required members/signatures/domains/effects are
   mechanically checked for the requested specialization.
2. **Law-tested:** named equations hold on a reported finite/generated sample.
   This is useful documentation and counterexample discovery, not a universal
   proof, especially for binary64 edge cases.
3. **Trusted for a transformation:** a builtin law, a deliberately supported
   proof/certificate, or an explicit API semantic relaxation justifies a specific
   rewrite under its precise domain, equality, effect and stability assumptions.

Do not import C++'s no-diagnostic-required behavior when semantic requirements
are violated. msync can offer deterministic sequential operations without
trusting laws, and add stronger APIs only where their meaning is clear. No
unchecked Bool law field should enable fast-math, callback reordering or removal
of selected errors.

## Implementation path and discriminators

1. Publish conventional source operation dictionaries and `fold_with` / sorting
   adapters using explicit dictionary arguments. This requires no syntax or IR
   change and immediately serves multiple algebras for one carrier.
2. Add the structural requirements kernel and diagnostics, reusing actual
   ScopeInfo/function specialization and storage contracts. Keep descriptors
   erased or lowered to ordinary operations before relocatable bytecode; do not
   serialize an AST or install a runtime typeclass solver.
3. Add a law-testing library over explicit finite Ranges, with failure witnesses
   and clearly defined domain/equivalence. Verify genuine mathematical laws
   separately from trace/mutation behavior.
4. Consider individual optimized algorithms only after a lawful, stable subset
   and an actual workload benefit are established. Existing sequential fold is
   not renamed into a reordered reduction.

Useful tests are missing/wrong member diagnostics; wrong argument/result/row
schema; nullable mismatch; two different operation dictionaries over Number;
generic structural Scope products; operation-field mutation; shared captured
cell mutation during lazy enumeration; compiler/editor preview isolation; the
Number associativity and signed-zero counterexamples; and dictionary overhead
before/after ordinary O2 specialization. A tiny script not using contracts must
not pay a global solver or validation pass.

## Primary references and their actual relevance

* [Wadler and Blott, *How to make ad-hoc polymorphism less ad hoc*, POPL 1989](https://doi.org/10.1145/75277.75283),
  together with the [Haskell 2010 report on classes](https://www.haskell.org/onlinereport/haskell2010/haskellch4.html#x10-680004.3),
  explains operation-oriented generic constraints. Explicit dictionary passing
  is the useful implementation connection, not a demand to add Haskell's open
  datatype/class/instance surface.
* [C++ draft semantic requirements](https://eel.is/c++draft/res.on.requirements)
  distinguishes satisfying expression constraints from modeling their semantic
  laws. [regular_invocable](https://eel.is/c++draft/concept.regularinvocable)
  likewise has semantic nonmutation/equality requirements beyond callability.
  Adopt the distinction, not C++ undefined/ill-formed-no-diagnostic behavior.
* [Rossberg, Russo and Dreyer, *F-ing Modules*](https://people.mpi-sws.org/~rossberg/f-ing/)
  gives compositional elaboration of module mechanisms into a smaller core;
  [1ML](https://people.mpi-sws.org/~rossberg/1ml/) shows the close relation between
  modules, records and functions. msync needs the explicit module/dictionary
  connection, not user type generation, sealing or the entire ML module calculus.
* [Winant and Devriese, *Coherent Explicit Dictionary Application for Haskell*](https://arxiv.org/abs/1807.11267)
  explains why global uniqueness/coherence matters when explicit dictionaries
  coexist with implicit instances. msync can avoid that machinery by making
  the algebra argument explicit and preserving witness identity where a data
  structure depends on a particular operation choice.
