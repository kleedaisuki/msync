# Explicit dependent casts

Run `dotnet run --project tests/StdlibCast` from the repository root. The probe
uses both an explicit native declaration and the public `$std::cast(value, Type)`
entrypoint, then checks source execution and both serialized IR stages at O0,
O2 and O3. It covers conversion results, source rejection, selected parse errors,
signed zero, Scope/Function identity, inferred target types and lazy Range items.

Type values participate in generic specialization keys: a function taking a
Type parameter can produce different correctly typed specializations for Number
and String without sharing the first call's return metadata.

Deferred callbacks read captured locations live. Once a captured Type value
determines a deferred cast's result domain, writes must retain a compatible
target. The compiler records this dependency on the binding or field's storage
descriptor. An incompatible later write reports E0213 at the write and relates
the cast that established the contract. Previously compiled deferred writers
are checked too; a new dependency invalidates ordinary cached specializations.
This does not freeze values or reject compatible reassignment. Copies of Type
values remain independent locations; Scope aliases share their field location.

The probe includes failed-submission rollback and isolated editor previews so
speculative analysis cannot publish a new dependency into an accepted REPL.
These contracts are compiler metadata, not a new artifact opcode or runtime
registry.

Number parsing uses invariant `NumberStyles.Float`, including the platform's
IEEE NaN/infinity handling. Boolean text accepts only the platform's
case-insensitive `true`/`false` forms, with surrounding whitespace; it never
uses nonempty-string truthiness. See the primary platform contracts for
[Double.TryParse](https://learn.microsoft.com/en-us/dotnet/api/system.double.tryparse?view=net-10.0)
and [Boolean.TryParse](https://learn.microsoft.com/en-us/dotnet/api/system.boolean.tryparse?view=net-10.0).
