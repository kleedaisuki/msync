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

## Dependent expression queries

Ten queries use the compiler's structural and callable information rather than
inventing runtime reflection on the closed `Type` kind:

| Query | Result and contract |
| --- | --- |
| `is_invocable(operation, witnesses...)` | Bool: the selected callable accepts the supplied argument schemas |
| `is_invocable_r(result, operation, witnesses...)` | Bool: invocation is valid and its result implicitly converts to the requested Type |
| `invoke_result(operation, witnesses...)` | Inferred result Type; invalid invocations produce `E0222` with the actual typing reason |
| `parameter_count(operation)` | Declared arity, or the minimum native variadic arity; null for an unknown/inconsistent signature |
| `parameter_types(operation)` | Range of declared parameter Types; unannotated generic parameters are Any; null when unavailable |
| `is_variadic(operation)` | Whether the callable has a native variadic tail, including callable Path/String process specifications |
| `has_member(value, name)` | Whether every value represented by the current non-null structural schema has this member |
| `member_type(value, name)` | Inferred member Type, or null when absent |
| `member_names(value)` | Declared member names in ordinal order |
| `member_count(value)` | Number of declared members |

These operands are **unevaluated**. A witness which is itself a Type denotes a
prospective argument type, such as `Number`; another expression supplies its
inferred structural/callable schema. Function bodies, argument blocks, Process
calls, transfers and endpoint authentication are not executed. The function
value naming the query is obtained normally; its tested operands are not run.
Parameter inspection reports declarations, not a universally quantified
higher-kinded signature or a layout/ABI promise. A Path/String process callable
is variadic with minimum arity zero and has no fixed `parameter_types` list.

```msync
let meta = $std::meta;
fn label(value) {
    if meta::has_member(value, "name") { value::name }
    else { value as String }
};
label(scope { name: "Klee" });
label(42);

let increment = fn(value: Number) { value + 1 };
let accepts_number = meta::is_invocable(increment, Number);
let result_type = meta::invoke_result(increment, Number);
```

Known results remain semantic facts during specialization, before bytecode
optimization. The generic branch above therefore checks the selected member or
conversion instead of typechecking both incompatible implementations. Ordinary
Type producers still retain effects: `is_same(compute_type(), Number)` evaluates
`compute_type()`, whereas `is_invocable(action, compute_argument())` only infers
the candidate argument expression.

A concept can participate through an ordinary constrained callable; there is no
runtime Concept value and no `Scope`-kind shortcut for structural satisfaction:

```msync
concept Named { name: String, };
let accepts_named = fn(value: Named) { value::name };
let valid = meta::is_invocable(accepts_named, scope { name: "Klee" });
let invalid = meta::is_invocable(accepts_named, scope { name: 42 });
```

Metadata inference uses a separate Compilation with shared immutable source AST
and code, copied semantic locations, and fresh specialization tables. It does
not reparse a generated source string or invoke an alternate evaluator. A
failed detection cannot reserve a stale Never specialization in the real
program or attach callable/concept contracts to its live locations. Scope
memory views, source provenance graphs and indexed Buffer element constraints
are cloned with their internal alias relationships intact. No query bytecode or
runtime AST is serialized; artifacts contain the resulting ordinary values.

The [C++ relationship traits](https://eel.is/c++draft/meta.rel) motivate
conversion and invocability questions. Its
[unevaluated invocation result](https://eel.is/c++draft/meta.trans.other)
motivates result inference, while msync uses its own source specialization
rules rather than C++ overload resolution or SFINAE. `enable_if`, pointer/CV/ref
transforms, trivial-copy/layout traits and nominal inheritance queries have no
corresponding semantics here and are not supplied as misleading no-ops.

## Accepted incremental observations

- `tests/MetaTypeValues` accepted 2,774 focused type-relation, transformation,
  runtime-native and staged artifact contracts. The source layer contains 13
  ordinary conveniences over 15 pure Type primitives.
- `tests/MetaTraits` accepted 292 dependent-query checks across O0/O2/O3 and
  source, serialized relocatable and serialized contextual executable stages.
  The additional Buffer probes cover successful and failed speculative writes
  followed by a different real first element type, plus memory-view isolation.
- `tests/Msync.EndToEnd/type_traits_journey.py` accepted nine real CLI routes,
  261 exact file contents, 27 absent forbidden effects and four diagnostics.
  It deletes source before binary execution and changes cwd after binding.
  Its recorded image is the managed Debug CLI in
  `.temp/meta-traits/build/bin/Msync.Cli/debug`; this is not a Native AOT timing
  claim. Native platform acceptance reuses the same focused journey.

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
