# Standard-library publication

Run `dotnet run --project tests/StdlibLoading -c Release`, then repeat with
`-- --demand-loading` and `-- --captured-modules`. These are real compiler, native
codec, contextual preparation and VM journeys, not source-text inspections.

The cases preserve cross-module calls, lazy static publication, replacement of
root/nested modules, captured namespace identity, fresh function-local state and
iterator behavior. Structural checks use native MakeFunction, Allocate and
PublishStatic instructions; obsolete generic ScopeValue constants are not an
alternate backend. Producer artifacts rejected by strict validation are retained
under `.temp/stdlib-loading-failures/` for diagnosis.

All demand-loading cases run O0/O2/O3, including root/module replacements.
The session mode additionally imports a public module before exposing `$std`,
then checks that root publication neither moves captured cells nor loses their
initialized values. Mutating a shared method affects both a retained alias and
the canonical module; replacing the module updates captured module reads while
preserving the old alias. The actual root layout must not expose private helper
modules. These controls accept initialized runtime values, not a simulated
namespace object. Live sessions remain non-exportable; source-free HIR/native
artifact routes cover self-contained programs without bypassing that boundary.

`--query-modules` isolates the query/storage invariant (also included in the
basic run): a `decltype` body can replace a public module using its unchanged
opaque Scope storage ABI, while its returned Scope still requires the real
`value` field. O0/O2 source/stripped-HIR/native-codec/bound routes return 7;
editor analysis rejects the wrong returned field, and a following real Bool
operation proves query-only mutations do not leak into the publisher.
