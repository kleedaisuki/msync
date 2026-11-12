# Nullable storage and current-value precision

`let value? = expression` admits later Null assignments; it does not add a Null
alternative to a currently non-null expression. The immutable storage constraint
and the current flow information have separate owners. A later assignment to
Null changes the current value and requires narrowing before a non-null use;
assigning an unrelated kind still violates the original storage constraint.
The same admission marker is available on a loop binding: `for value? in
stream` admits Null, while an unmarked binding still rejects a nullable
element. Marking a binding does not narrow or widen the value yielded on a
particular iteration, and does not change when the stream is consumed.

The focused journey covers ordinary calls, generic results, captured writes,
Null assignment and subsequent refinement/reassignment at O0/O2/O3. Each program
executes directly, after relocatable serialization, and after executable binding
and serialization. A nullable loop alternates a lazy producer's visible
effects with its body, and a marked loop over only Numbers still passes a
Number to a non-null parameter. Negative cases check known-null arguments,
incompatible assignments and an unmarked nullable loop.
An unrelated structural Scope summary also remains independent of the record
whose member was narrowed in a preceding loop branch.

Run `dotnet run --project tests/NullableFlow/Msync.NullableFlow.Tests.csproj`.
Add `-- --indexed` to include the actual vector get-to-set sequence that exposed
the defect in `containers::Heap` sift-down. The indexed case requires the current
storage module; the general flow journey is independent of that module.
