# Nullable storage and current-value precision

`let value? = expression` admits later Null assignments; it does not add a Null
alternative to a currently non-null expression. The immutable storage constraint
and the current flow information have separate owners. A later assignment to
Null changes the current value and requires narrowing before a non-null use;
assigning an unrelated kind still violates the original storage constraint.

The focused journey covers ordinary calls, generic results, captured writes,
Null assignment and subsequent refinement/reassignment at O0/O2/O3. Each program
executes directly, after relocatable serialization, and after executable binding
and serialization. Negative cases check known-null arguments and incompatible
assignments rather than relaxing downstream function contracts.

Run `dotnet run --project tests/NullableFlow/Msync.NullableFlow.Tests.csproj`.
Add `-- --indexed` to include the actual Buffer get-to-set sequence that exposed
the defect in `containers::Heap` sift-down. The indexed case requires the current
storage module; the general flow journey is independent of that module.
