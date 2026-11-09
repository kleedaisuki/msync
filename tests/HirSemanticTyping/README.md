# Typed HIR semantic typing journeys

This executable uses `Compiler.Analyze` directly, lowers its immutable result,
serializes the bytecode and executes both relocatable and context-bound images.
It does not use the old `Compiler.Compile` path as a substitute for HIR analysis.

The focused programs cover scalar operators, explicit casts, implicit Path/String
representation conversion, Null tests, Scope aliases and fixed fields, concept
requirements, actual typed native callbacks, indexed storage and stable snapshots.
Lazy callback journeys distinguish constructing a Range from enumerating it,
including callbacks that mutate captured cells or Scope fields and Ranges
returned through source functions. An eager consumer must invalidate prior
flow facts; an unused lazy Range must not change them.
Rejected programs distinguish type and null-admission failures before lowering.

## Deferred callback ownership

Semantic analysis specializes a callback to establish its argument/result types
and records projected writes to captured locations, object fields and indexed
resources. Constructing a deferred Range retains this summary, but does not
apply it to the caller's flow facts. Lazy compositions carry the summaries of
the inputs they actually enumerate; wrapping a Range as an item does not
enumerate it. Source-function return projection renames summary targets along
with the returned value graph.

An eager consumer or explicit loop applies a zero-or-more transfer: join the
before/after domains and discard known scalar values. This keeps empty inputs
valid and prevents a callback's specialization from being mistaken for an
execution. The same signatures supply HIR effects; semantic analysis never
enumerates values or performs I/O to establish these facts.

```text
dotnet run --project tests/HirSemanticTyping -c Release
dotnet run --project tests/HirSemanticTyping -c Release -- --meta
dotnet run --project tests/HirSemanticTyping -c Release -- --type-is
dotnet run --project tests/HirSemanticTyping -c Release -- --indexed-union
dotnet run --project tests/HirSemanticTyping -c Release -- --indexed-cast
dotnet run --project tests/HirSemanticTyping -c Release -- --range-identity
dotnet run --project tests/HirSemanticTyping -c Release -- --nested-effects
dotnet run --project tests/HirSemanticTyping -c Release -- --native-effects
```

Use an isolated artifacts path while other implementation lanes build the shared
workspace. The fixture is an incremental semantic-boundary check, not a full
product regression or a replacement for the interactive/native acceptance lanes.

The metadata-only slice covers invocation/result queries, declaration arity and
parameter types, structural member queries, recursive and invalid unused generic
bodies, and non-execution of metadata operands. It round-trips the semantic
graph through `.mhir` before lowering, and checks that dependent queries are
resolved rather than emitted as runtime native calls. This slice can run alone
when only metadata analysis changes.

The Type-identity slice retains runtime Type expressions on both sides of `is`,
including loop-carried Type values. It checks left-to-right operand effects,
unknown represented types, and static `decltype` identities through the same
`.mhir` round-trip boundary. A dynamic Type is a value, not a request to execute
the right operand in a speculative query context.

The indexed-resource slice combines independently allocated Buffers stored in
different Scope rows, then reads and mutates a selected row's Buffer. The flow
join retains a finite domain of possible resource identities, not a new merged
resource. Reads join their element facts; writes must satisfy each possible
target's fixed carrier and update the corresponding alias facts. A union with
an ordinary Range does not acquire the Buffer capability.

The indexed-cast slice selects between Path-backed and String-backed resources,
checks the actual stored representation after push/set/insert, and checks that
first-write inference preserves existing membership snapshots and hash identity.
String-to-Path conversion retains the runtime directory/home context; the
opposite conversion uses the selected Path's native spelling.

Range identity tests distinguish a true identity from a derived view with the
same element carrier. Reverse/filter/materialize do not acquire mutable Buffer
ownership; references stored as elements still retain their own identities.
Nested-effect tests distinguish consuming an outer Range from enumerating an
inner Range returned as one of its values.

Native-effect checks exercise the user-visible `check` behavior on a nonexistent
Process path without running it, inspect the Process read's semantic effects,
and retain a discarded invalid native cast's actual error through O2 execution.
The registry supplies complete effect metadata to both semantic and bytecode
consumers rather than requiring frontend bytecode queries.
