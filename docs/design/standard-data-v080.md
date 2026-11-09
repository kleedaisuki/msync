# Algebraic data composition with closed runtime kinds

The November 9, 2026 standard-concepts follow-up is about algebraic **data**
composition, not primarily a catalogue of groups and rings. The language keeps
its closed runtime kind set. Named Scopes organize families, anonymous
Scopes hold data, and generic functions specialize on the actual row, callable
and selected policy. Concepts describe structural requirements, not new runtime
classes, global instances or a higher-kinded type system.

The canonical source concepts use inline associated binders:

```msync
concept Product {
    first?: typename First,
    second?: typename Second,
};
concept Option {
    value?: typename Item,
};
```

`typename` introduces an associated name at its first requirement. It does not
declare a runtime kind. Subsequent requirements can refer to that name; the
checker derives its actual carrier and structural row from the supplied value.
The optional marker still describes admitted Null storage, independently of
the associated name.

## Products: ordinary records and explicit operations

`$std::data::Pair::new(first, second)` produces an anonymous Scope with directly
accessible `first` and `second` fields and associated carrier witnesses. There
are no per-instance getter, setter or method closures. Named module operations
take the record explicitly: `swap`, `map_first`, `map_second`, `bimap`, `fold`,
`curry`, `uncurry`, and `equal` with two explicit Equality policies.

The fields follow the language's normal rules: values can change, storage types
cannot, aliases share the same record, and nullable payloads require admitted
nullable storage. Mapping constructs a fresh record; it does not mutate the
input or deep-copy payload objects. The public families are actual named Scope
declarations in `stdlib/data.msync`, not function factories pretending to be
classes or aliases of an older flat module. Family names use PascalCase;
operations use lower_snake_case, for example `Pair::map_first`.

## Optional values: use the existing nullable sum

Absence is the language's actual `Null`. A present value is the product
`scope { Item: decltype(value), value?: value }`. Thus:

| Expression | Meaning |
| --- | --- |
| `$std::data::Option::none()` | Absent value, equal to Null |
| `$std::data::Option::some(3)` | Present Number payload |
| `$std::data::Option::some(null)` | Present Null payload, not absence |
| `value: $std::concepts::Option?` | An admitted absent-or-present value |

Use normal exhaustive nullable control flow rather than a second tag system:

```msync
let selected? = $std::data::Option::some("Klee");
let message = match selected {
    null => { "absent" },
    _ => { selected::value },
};
```

The `Item` carrier witness retains the payload type even for `some(null)`.
Its runtime representation remains an ordinary Type value, not a user-defined
type object. A present object's nullable payload can subsequently become Null
without changing presence. `to_nullable` intentionally loses the distinction
between an absent option and a present Null payload.

The Option family provides `some`, `none`, `is_some`, `is_none`, `map`,
`and_then`, `fold`, `get_or`, `get_or_else`, `or_else`, `filter`,
`from_nullable`, `to_nullable`, `flatten`, `zip`, and `zip_with`. Mapping and
binding invoke their callback only for a present value. `get_or` receives an
already evaluated fallback; `get_or_else` and `or_else` invoke a fallback
callback only for absence. A declared nullable return preserves the narrower
actual row or actual Null through specialization; an annotation does not
fabricate a missing payload row.

## Binary sums: preserve payloads through an eliminator

The current Scope kind has no correlated literal-tag variant analysis. A
record containing `tag`, `value?` and `error?` would require dummy payload
prototypes or would silently lose correlation during row joins. Result and
Either therefore use one generic `fold` closure as the foundational sum
eliminator. The closure captures only the selected payload, without a duplicate
mutable discriminator. All other operations belong to the named family Scope.

`$std::data::Result::ok(value)` selects the first callback and
`$std::data::Result::err(error)` selects the second. `Result::fold(value, on_ok,
on_error)` requires both alternatives as arguments. `is_ok` and `is_err` are
derived through that eliminator; a Boolean flag cannot disagree with it.
`map`, `map_error`, `bimap`, `and_then`, `or_else`, `get_or`, `get_or_else`,
`to_option`, `error_option`, `from_option`, `flatten`, `zip` and `zip_with`
are ordinary specialized family operations. Bind/flatten results must satisfy
the Result concept, so an accidental scalar result is rejected during
compilation. Unselected callbacks are not executed.

Either uses the same binary-sum representation with neutral left/right
terminology: `left`, `right`, `fold`, `is_left`, `is_right`, `map_left`,
`map_right`, `bimap`, `swap`, and `merge`. The `Result` and `Either` concepts
intentionally share the `Sum` eliminator capability. They are structural
protocols, not nominally distinct runtime types.

This is a practical application of the
[Boehm-Berarducci fold encoding](https://www.okmij.org/ftp/tagless-final/course/Boehm-Berarducci.html)
and [algebraic datatype encodings](https://okmij.org/ftp/tagless-final/datatypes.html).
msync does not claim their universal polymorphic typing theorem: its Function
values specialize at ordinary call sites, rather than expressing System F or
universal higher-kinded signatures. Option deliberately uses the simpler native
Null sum instead of paying for an eliminator closure per present value.

## Capabilities, policies and generic algorithms

`Mapper`, `Bindable`, `Folder`, `Traversable`, `Callable` and `Predicate`
constrain the corresponding operation fields. Their dependent callbacks remain
Function-valued fields; ordinary specialization checks the concrete callback
arguments and results when invoked. Merely satisfying `Mapper` is not a proof
of functor laws, callback purity, effect commutativity or permission to reorder
IO. A source module is itself an ordinary named Scope and can be passed as an
operation family to a generic algorithm.

First-order policies can state stronger callable contracts directly. The same
standard module provides associated-carrier `Equality`, `PartialOrder`,
`TotalOrder` and `StrictOrder`; `RangeStorage`; data contracts `PathRecord`, `CommandSpec`,
`CompletedProcess` and `PatternSpec`; and `TextIOPolicy`, `TextFile`,
`TextChunks` and `TextBuffer`. These are reused by the native family interfaces
in the [standard-library architecture](stdlib-v080.md#native-families-records-and-policies),
not a parallel JavaScript-style object façade. The small Magma/Semigroup/Monoid
constraints remain useful supplementary reduction protocols; they do not make
IEEE Number arithmetic an exact mathematical monoid.

The organization follows the explicit operations and generic bounds familiar
from [Rust generic data types](https://doc.rust-lang.org/book/ch10-01-syntax.html),
while retaining msync's own Scope/Fn metaprogramming. Operation-family records
also connect to [object algebras](https://www.cs.utexas.edu/~wcook/projects/oa/oa.pdf):
algorithms depend on selected operations rather than a runtime class hierarchy.
No standard-library record must be constructed merely to use a native Path,
Process, Range or existing nullable value.

## Focused acceptance

`tests/Msync.EndToEnd/standard_option_journey.py` exercises all sixteen Option
operations through real source, source-free relocatable bytecode and contextual
executable bytecode, including a different launch directory for bound execution.
The local O0/O2/O3 owner run accepted nine executions and 225 exact output
observations, plus four invalid callback/payload programs. It includes present
Null, nested options, mixed-payload zipping, and zero effects from unselected
callbacks. `.temp/standard-option/results.json` records the accepted managed
binary/assembly identities. After naming was settled on actual PascalCase
family Scopes and snake_case functions, an incremental O2 rerun accepted all
three stages and 75 exact outputs, recorded in
`.temp/standard-option/final-snake-results.json`.

`tests/Msync.EndToEnd/standard_data_journey.py` owns the complementary Product,
Result and Either workflows and their real constraint failures. The owner run
accepted 27 real executions at O0/O2/O3, 423 exact outputs and six rejected
programs, including both runtime-selected sum alternatives and zero effects
from inactive callbacks. `.temp/standard-data/accepted.json` records that run;
the final snake_case naming rerun accepted nine O2 executions and 141 exact
outputs in `.temp/standard-data/final-snake-results.json`. These workflows
exposed a generic nested-function recursion-guard collision; it was corrected
in the compiler's callable/Scope-shape specialization logic rather than hidden
with a library workaround. Final Native Linux/Windows acceptance uses the same
selected journeys rather than repeating the entire previously accepted library
matrix.
