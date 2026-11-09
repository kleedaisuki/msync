# Closed-type metaprogramming (v0.8.0)

## Type values, relations and transformations

`$std::meta` supplies the applicable families of C++ `type_traits` without
inventing pointers, references, cv-qualifiers, ABI layouts, nominal inheritance
or additional runtime types. The
[C++ primary categories](https://eel.is/c++draft/meta.unary.cat),
[relations](https://eel.is/c++draft/meta.rel) and
[transformations](https://eel.is/c++draft/meta.trans) provide the useful division
of responsibility, not a set of names to copy regardless of semantics.

Every operation below accepts ordinary built-in `Type` values, including
`decltype(expression)` and parameterized Range types. These values describe the
closed type universe; a Type value for Scope does not encode member contracts
or prove satisfaction of a concept.

| Family | Operations | Meaning |
| --- | --- | --- |
| Primary kinds | `is_null`, `is_bool`, `is_number`, `is_string`, `is_path`, `is_range`, `is_function`, `is_scope`, `is_process`, `is_connection`, `is_type`, `is_never` | Exact outer kind; `Number?` is not classified as Number |
| Composite kinds | `is_union`, `is_scalar`, `is_nullable` | Null counts as a union alternative; scalar alternatives are Bool, Number, String, Path, Connection and Type, excluding Null and Never |
| Relations | `is_same`, `accepts`, `is_assignable`, `is_convertible`, `is_castable` | Equality; exact storage admission; assignment admission; implicit conversion; legal explicit `as` conversion |
| Transformations | `add_null`, `remove_null`, `common_type`, `range_of` | Idempotent null admission/removal, finite union, and construction of a Range type |
| Inspection | `element_type`, `range_rank`, `type_name`, `alternatives` | Range element, nested Range depth, diagnostic spelling and stable builtin-kind alternatives |

`accepts(target, source)` and `is_assignable(target, source)` use destination
first. `is_convertible(source, target)` and `is_castable(source, target)` follow
conversion order. Assignability is the implicit conversion relation because
all admitted storage bindings are mutable; it is not C++ reference assignment.
Path/String is the only implicit conversion pair. Range element conversions
are not implicit. `is_castable` reports legality of a conversion, not whether
particular runtime text parses as a Number or Connection.

`common_type` retains both possible values as a finite union instead of
silently preferring a scalar representation. Range unions retain their element
union. `element_type` accepts a Range or nullable Range and returns its element
Type, Any for an unspecified element, or null for a non-Range. `remove_null(Null)`
produces the empty Never type. `alternatives` retains parameterized Range
elements and orders alternatives by the stable builtin kind order, starting
with Null when present.

The primitive layer shares `MetaTypes` between compiler static evaluation and
runtime evaluation; artifact execution does not need source replay. Exact kind
classifiers and related conveniences compose the small primitive vocabulary in
ordinary standard source functions rather than introducing one host primitive
per public operation: 15 pure host operations support 28 public Type operations,
with 13 conveniences implemented in source. Pure Type operations preserve
ordinary argument evaluation. Use `decltype` when an input
expression must be inferred without executing it; the module does not secretly
turn every ordinary function argument into an unevaluated operand.

The focused executable in `tests/MetaTypeValues` records scalar classification,
type relations, nullability and union laws plus actual O0/O2/O3 source,
serialized `.mso` and contextual `.mexe` execution. It does not introduce timing
thresholds or unrelated full-regression dependencies.

Known Type arguments retain their denoted type through these operations. A
predicate over `decltype(value)` therefore selects the valid specialization
branch before lowering, including at O0:

```msync
fn adjust(value) {
    if $std::meta::is_number(decltype(value)) {
        value + 1
    } else {
        $std::string::concat(value, "!")
    }
};
adjust(4);       # Number result: 5
adjust("Klee");  # String result: "Klee!"
```

The actual CLI acceptance fixture is
`tests/Msync.EndToEnd/type_traits_journey.py`. Its local managed observation
(`.temp/meta-traits/journey.json`) accepted nine source / source-deleted `.mso` /
source-deleted `.mexe` routes at O0/O2/O3, with 29 exact outputs per execution
and four useful negative diagnostics. The fixture checks all 38 public Type and
dependent metadata operations together, including heterogeneous specialization,
policy fallback, function/capture inspection, prospective SSH process calls and
metadata operand blocks containing writes. The tested function/capture counter
stays zero and three forbidden write targets remain absent. Contextual artifacts
run from a different launch directory. This is managed integration evidence;
Native AOT platform acceptance uses the same fixture against each built image.
