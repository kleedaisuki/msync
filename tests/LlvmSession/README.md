# Explicit LLVM-derived source-session journey

```sh
dotnet run --project tests/LlvmSession -c Release -- --output artifacts/diagnostics/llvm-source-session
```

This isolated friend-harness project uses the existing source binder/analyzer and
accepted semantic facts, then `LlvmHirLoweringSession.Lower/Emit` and the existing
`NativeExecutionSession`. It does not change default Compiler/SemanticSession
callers or fall back to their old Native lowering/optimizer paths. Fresh raw and
explicit mem2reg/DCE sessions each accept fifteen real source submissions.

The source flow retains mutable lexical captures, Scope aliases, escaped
closures and generic callable aliases; changes a deferred range callback target
between submissions; and shadows a root without rebinding existing captures.
A test-owned local shell child waits for a file gate, so its stdout is a genuinely
pending future while accepted roots and a captured alias cross another source
submission. Acceptance must not demand that future. Releasing the gate then
consuming the retained future must write exactly `pending-value`. The gate is
released on failure too, so this fixture does not strand its child process.
Sources, typed HIR, before/after IR, per-submission results and final receipt stay
inside the selected output directory. These are source-session behavior checks,
not a full REPL migration or a runtime-speed claim.

## Production CompilerSession acceptance and reconciliation

```sh
dotnet run --project tests/LlvmSession -c Release -- --production-session --output artifacts/diagnostics/llvm-production-session
```

This separate entry exercises the actual public `CompilerSession.Compile`,
`AcceptSubmission` and `ReconcileSubmission` APIs with `NativeExecutionSession`;
it does not manually reproduce semantic lifecycle or call a selected lowerer.
O0/O2 each run twelve submissions. Runtime initializer failures must retain the
initialized prefix and alias writes, exclude failed/unreached declarations, and
preserve the old binding after a failed shadow initializer. Another failure
occurs while a gate-held stdout future has been assigned to a new root; the
pending alias must count as initialized without being demanded, survive actual
production reconciliation, and later write the exact future result. Existing
mutable captures must survive later successful shadowing too.

Each submission retains source, accepted/reconciled bindings, actual result,
compiler trace and concrete error diagnostic. Traces must show the primary
LLVM-derived producer/target and must not enter the former Native optimizer.
The original thirty accepted-submission harness remains a separate entry; this
option does not rerun it or substitute it for production reconciliation.

The primary-IR migration run passed through these actual production APIs at O0
and O2: each mode completed 12 submissions (9 accepted and 3 reconciled runtime
failures), 6 exact numeric results, 3 genuinely pending-root observations, and
1 exact future-output file. Receipt and parent-serialized build/run logs are
retained in `artifacts/diagnostics/llvm-production-session/`. No re-execution of
the already accepted thirty-submission manual harness was needed for this slice.

## Prepared execution-image ownership

Run the isolated source/bind/Native2 roundtrip fixture with:

```sh
dotnet run --project tests/LlvmSession -c Release -- --prepare-image --output artifacts/diagnostics/llvm-prepare-image
```

It requires actual contextual selection/transfer rewriting into ExecutePlan,
checks that only the entry backend image changes, then adds a file after bind
and executes the saved bound image to check frozen selection membership.

## Pending Number and zero-trip LICM behavior

```sh
dotnet run --project tests/LlvmSession -c Release -- --licm-pending-only --output artifacts/diagnostics/llvm-licm-pending
```

This separate O0/O2 entry uses three actual public-session submissions per mode.
A gate-held child process exposes an `exit_code` Number future. Calling a
zero-trip arithmetic kernel with that pending Number must return zero while the
root is still pending; acceptance and any pure arithmetic motion must not demand
it. Only afterwards is the test-owned gate released and the Number consumed,
with the eventual zero exit result retained. Source/type spelling follows the
existing Process capability contract, not a new language operation. The gate is
always released in `finally`. Sources, compiler traces and result receipts remain
under the requested output directory. This entry does not rerun the previously
accepted source-session or production-reconciliation journeys.

The parent-serialized pending-Number run passed through the actual production
CompilerSession at O0 and O2: three submissions per mode; zero-trip result zero;
two genuinely pending-root observations before the gate was released; and the
retained Number subsequently resolved to zero. Receipt and logs are in
`artifacts/diagnostics/llvm-licm-pending/` (also
`.temp/llvm-ir/licm-pending-{build,run}.log`). This validates that the observed
LICM arithmetic motion did not turn an unentered loop into a demand for its
pending numeric operand. No previously accepted session harness was rerun.
