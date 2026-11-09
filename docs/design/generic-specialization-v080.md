# Higher-order specialization and recursive inference

Generic functions specialize over semantic argument and capture information,
not only runtime `TypeKind` values. A `Function` argument includes its concrete
callable identity; a `Scope` argument includes member schemas and their callable
identities. Associated Type values and Range element information also affect
the specialization. Scalar literal values do not specialize ordinary generic
functions.

## Generated standard-module captures are reads

Source expressions such as `$std::containers::List::size(self)` capture a
compiler-owned module location named `$std::containers`; a materialized root
instead captures `$std`. These generated names are absent from the source-name
walk, which sees `std`. They must nevertheless contribute their complete
semantic capture information to the specialization key. Treating them as
write-only locations erases member/callable changes after a whole module or
family replacement and can invoke the previous body with a new closure's
capture layout.

The generated dependency representation establishes these as reads; ordinary
write-only lexical captures still omit their old callable identity to preserve
convergence for recursive stores. This does not merge detached namespace aliases
or reload a module. `tests/StdlibLoading --captured-modules` checks warm calls
before and after replacement, retained aliases, callee evaluation before a
mutating argument, nested closures and lexical `std` shadowing. Its 81 focused
contracts cover O0/O2/O3 source, `.mso`, `.mexe` and REPL execution.

## Nested calls are not recursive edges

Consider a generic `apply(operation, value)` invoking a callback that itself
calls `apply` with a different callback. Both calls may have parameter kinds
`(Function, Number)`, but they require different specialized bodies. Reusing the
unfinished outer body produces a spurious `Never` result. The same mistake
affected nested sum elimination in the standard `Result::zip` implementation.

Active specializations are therefore stacked per callable template. An active
body closes a recursive edge only when its argument and capture shapes match:
types, callable identities, Type values, Range elements, and recursively checked
Scope members and storage constraints. Scope comparison tracks visited schema
pairs so recursive records do not require a depth limit. Inner calls with
different callbacks retain their own active frame; returning or failing restores
the previous frame instead of deleting the outer context.

## Reserved identities survive later rendering

Recursive calls can construct a fresh Scope with the same schema. Their exact
metadata identities differ, but the active semantic shape identifies the same
recursive body. The compiler records that exact key as an alias of the reserved
specialization. Mutable-call finalization can then revisit the edge after the
active inference frame has finished without repeatedly creating equivalent
recursive bodies. This is a fixed-point identity, not an iteration cap.

Editor inference follows the same distinction. A shared function syntax node
does not by itself mean recursion: nested generic calls must retain their actual
result schema for hover and completion. The editor's recovery guard consults
the active semantic shapes rather than a syntax-only membership set.

## Returned values and deferred reads

A function returns a value, not an alias of a private parameter or local binding
cell. The compiler detaches that top-level storage provenance at the return
boundary while retaining the actual value type. An explicitly nullable local
whose selected value is Number therefore does not turn a returned Number into
`Number?`. Scope instance identity, captured cells and indexed-memory provenance
remain intact; this is not a deep copy or blanket removal of storage metadata.
External captured locations retain their callable result contracts.

Deferred calls have a different observation boundary. Free reads compiled into a
Range thunk use the legal future domain of mutable captured locations, rather
than a type refinement that held only when the adapter was constructed. The
fresh callee body then analyzes its own control-flow edges normally. This
distinction lets a source `lines` function match String, Path or Range input
and return Range in every case, including when an external argument changes
alternative before consumption. A generic identity over String or Range is not
always Range: if the deferred pass finds that union, it emits a normal call with
the union result instead of an invalid `DeferRange` instruction.

An unwritten formal parameter has a private by-value cell whose current value
cannot change. A cached lexical write analysis recognizes that fact, including
nested closures, `global` imports and shadowing by parameters, locals, loop
patterns and Scope fields. Its admitted storage type can be wider without
invalidating a branch refinement. This is not source-level immutability: an
actual assignment or captured setter keeps the full mutable domain. A stable
parameter containing a Scope does not make that Scope's fields immutable.

The scan runs once per function syntax and parameter. Cache hits do not allocate
its recursive traversal closure, and scalar deferred reads do not allocate the
Scope-widening dictionary. Argument and callback evaluation remain on demand;
the compiler does not freeze an external live cell to recover a type proof.

## Focused acceptance

`tests/HigherOrderCalls` exercises eighteen programs through source, serialized
relocatable bytecode and serialized contextual executable bytecode at O0/O2/O3.
The 162 executions cover nested and triple application, Result composition,
recursive fresh-Scope arguments, callable parameters, private return values,
live captured mutations, union dispatch, lexical shadowing and captured
Path/String conversion. Eighteen corresponding editor analyses assert the exact
inferred result type. Twelve checks reject stale deferred refinements involving
live unions, written parameters, captured global writers and mutable Scope
payloads. All 192 focused contracts passed on the isolated managed product.

The standard-library owners separately run actual public Result composition,
Heap drain/map/join and terminal pipelines. These focused compiler checks do not
replace those API journeys or Native AOT acceptance.

## Captured record values and nullable admission

An admitted storage domain is not an additional value produced by a constructor.
For example, `Result::err(error?)` accepts Null, but passing a non-null record
does not make that captured record nullable when the constructor parameter is
never assigned. Later callback-contract checks must retain that distinction.
The parameter-write analysis includes nested closures and lexical shadowing;
a real captured setter therefore keeps the location's full nullable domain.

A Null write also does not erase a captured location's established record schema.
When checking a writable captured record after its current value becomes Null,
the compiler retains the captured declaration's row while widening its value
domain. Mutable member facts still undergo the normal capture widening. A
subsequent different record is checked against the established callable contract
rather than being admitted through a shapeless `Scope?` fallback.

Callable diagnostics render nullable record results as `Scope { ... }?`. Dropping
that suffix previously made an actual nullable/non-null mismatch appear to have
identical expected and actual field lists.

`tests/CallableResultContracts` covers record-valued Result elimination followed
by `get_or`, genuinely writable nullable constructor parameters, and incompatible
rows/nullability. Its 63 checks pass through source, serialized `.mso` and
serialized `.mexe` at O0/O2/O3. The actual command-policy source additionally
checks at all three levels without launching its child processes; actual live
Process execution is a separate ownership/terminal journey.
