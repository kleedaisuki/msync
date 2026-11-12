# Returned Process ownership

This focused real-process harness invokes itself as an stdin-gated child. It
checks source, serialized relocatable bytecode and bound/source-free executable
bytecode at O0/O2/O3, without shell syntax or timing sleeps:

- returned Process values, Scope fields, captured functions and lazy/eager Ranges;
- live processes stored through an outer `global` cell or original Scope argument;
- dependent stdout transfers, including a flow around an incoming Process;
- work that does not escape still joins before the invocation completes;
- persistent REPL globals survive separate submissions, while block temporaries
  and reassigned private parameters do not become session roots;
- lazy producers from an older interpreter context give their new work to the
  actual active caller;
- error reconciliation, explicit wait and session disposal own actual children;
- materialized and vector snapshots retain output membership rather than input
  ranges or discarded owner contents.

A small control-null transport test supplies a real completion Task and an
owned-abort capability, checking session disposal without manufacturing a remote
PID or requiring another SSH fixture. The live local cases use actual Process
handles throughout.

```text
dotnet run --project tests/ReturnedProcesses -c Release
```

`--session-only`, `--external-only`, `--late-cell-only` and `--flow-failure-only` select new contracts
without repeating already accepted routes. `--no-io-baseline PATH/TO/Msync.Runtime.dll`
loads a frozen runtime with the same current Core and executes the same 256-call
bytecode in ABBA order. A zero-call control separates per-entry context changes
from per-function allocations. This is a managed allocation probe, not a native
latency benchmark or a CI timing threshold.

`--native-prefix-suspension` checks a different ownership boundary:
an indexed method hands off from guest machine code to a canonical effect
which really waits on its injected input reader. The gate is signalled
before a result is made available. Source, standalone portable and bound
paths check that the selected print/read happen once and that the live
numeric local survives. A separate bound path cancels the incomplete
read; it must not replay the print. Repeat with `MSYNC_JIT=off` to
check the same behavior without the native handoff.

Aggregate profiling counts actual dispatcher pauses and allocated
continuations separately (`dispatch_pauses`, `dispatch_continuations`).
A frame reuses its continuation for later incomplete operations. These
metrics do not count completed ValueTasks or imply one continuation per
logical call.

`MSYNC_JIT=auto dotnet run --project tests/ReturnedProcesses -c Release -- --indexed-roots`
checks reference traversal rather than sequence evaluation. A function returns
two vectors: one containing a live stdin-gated child, and one containing a
lazy Range with an observable producer. The caller snapshots the child vector,
clears its owner and exchanges data with the original child. Direct source,
serialized portable and bound routes assert the exact response and that the
sibling producer was never invoked.

`--arena-roots` checks the collection-owned Set representation. A function
returns a Set whose only key is a live stdin-gated Process. The caller takes
an ordered snapshot, erases the key, and exchanges data with the original
Process through the snapshot. Direct source and both binary round trips
must produce the exact child response; the owner never publishes a node.
