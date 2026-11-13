# Native persistent session journey

Run `dotnet run --project tests/NativeSessions` from the repository root.

This uses canonical `CompilerSession` with `NativeExecutionSession`, without
translating through historical runtime values. `NativeCompilerSession` is also
checked as a compatibility name for the same compiler implementation. The journey
checks shared namespace aliases, mutable
captures, declaration shadowing, Type witnesses, generic callable specialization,
configured stdin, accepted path editor projections, definition-context path
literals and deferred callbacks across `cd`, editor-only specialization isolation,
mutable deferred callable replacement with fresh closure captures, ambient versus
explicit directory ownership, deferred line/byte readers surviving a native `cd`,
prefix exports after a real filesystem failure, and physical capture storage
invariants when a nullable field receives its first non-null value. On POSIX it
also returns a live `cat` Process from a function, writes stdin in a later submission and waits
in a third, so a submission boundary cannot silently join or abort escaped work.

The actual terminal acceptance remains
`tests/Msync.EndToEnd/repl_state_journey.py` and
`tests/Performance/repl_tail.py`; this direct session journey makes failures
inspectable without a terminal renderer.

`MixedCallableChecks` also executes finite native/source callable alternatives
from source, source-text-free standalone HIR and detached native artifacts at
O0/O2, with independent results for both filesystem-controlled branches. It
covers all source builtin result kinds, namespace/Range/Function results,
captured state, callee evaluation before argument mutation, exactly-once
computed callees and higher-order native callback adaptation. Persistent
sessions then replace compatible callees, including filter/take-while callbacks.
Internal descriptor comparison is not a new source comparison operator.

## Verified integration paths

The native cutover also runs the existing redirected REPL mutable-prefix/failure
journey and the exact-source callable-order journey at O0/O1/O2/O3. A colored
PTY prompt run with 32 growing exports checks actual render/read/submit behavior;
it is functional acceptance, not a performance comparison against historical
builds. Instruction-level SQLite telemetry records native `vm.function`,
`vm.call`, `vm.instruction` and `vm.edge` observations across submissions.
