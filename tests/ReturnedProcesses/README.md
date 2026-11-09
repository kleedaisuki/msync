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
- materialized and Buffer snapshots retain output membership rather than input
  ranges or discarded buffer contents.

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
