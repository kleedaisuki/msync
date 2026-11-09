# Session dispatch runtime contract

These source-independent bytecode journeys exercise the runtime boundary for
open REPL calls. A deferred value created in one accepted generation reads a
shared callable location after another generation assigns a new definition.
The same signature slot resolves through the current session image and retains
the new closure's captures. Separate sessions do not share tables, and a resolved
deferred Range remains once-on-demand. Additional journeys distinguish argument
signature slots, retain an assignment completed before a later runtime failure,
and invoke a newly registered process-producing target whose nonescaping work
must finish before its caller returns.

Negative `Call.B` operands encode `-signatureSlot - 1`; ordinary nonnegative
operands keep their format-4 meaning. A deferred native callback uses the same
encoding in `SpecializeClosure.A`, selecting its typed target only when the
callback value is adapted. The complete dense table lives only in
`Module.SessionDispatches`. Portable artifact export rejects that live-session
dependency rather than silently dropping it. Existing closed artifact format
4 and language-semantic version 0.8.0 are unchanged.

Run with `dotnet run --project tests/SessionDispatchRuntime -c Release`.
