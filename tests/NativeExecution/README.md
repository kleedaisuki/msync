# Native IR execution

This host compiles actual language programs with `Compiler.CompileNative`,
including native optimization, and executes the register/address IR with
`NativeInterpreter`. It does not adapt to the retired Value/Scope object
interpreter. Mixed builtin fields,
aliases, shared captures, captured parameters, recursion, lazy stdin, actual
publication and a returned child blocked on its input are independently scored.
A detached artifact additionally checks dynamic repeated allocations, index
addressing, overlapping memory copies, snapshot Range membership and clearing.
Selected fields of array-of-structures rows are checked through both copied
snapshots and proven owned-region transfer.
The snapshot must remain unchanged after its source region is cleared, including
an unresolved transfer stored/copied before demand. A weak-allocation GC oracle
ensures future side metadata does not retain unreachable logical allocations.
A separate detached artifact reads a captured value through the explicit environment address ABI.

Real missing-file transfers check both ignored and explicitly handled failures counted once, while retaining false
results and concrete diagnostics, matching the frozen format-6 behavior. Process journeys retain a
child through a returned closure, terminate owned children at interpreter
disposal, and cancel an actual child wait. Profiling checks executed instruction
and site totals, selected callees, builtin calls and logical allocations against
the executed source, rather than asserting historical worker metrics. An actual
suspended frame is resumed after `UpdateProgram`: it must use the old image's
constants while subsequent calls use the new generation, and profiling must
pair the observed suspension with its continuation. A real private block child
also checks that retaining canonical session storage does not implicitly promote
nonescaping work: the submission must join that child before returning.
Controlled native interop failures check that one iterator disposal failure does
not skip other cursors, and a failed future does not skip an owned Process join.
The original concrete errors remain observable.

`--pointer-storage` selects the strict-artifact pointer workflows: namespace
rebinding, indirect and cross-callee pointer writes, a physical monomorphic
pointee reloaded through a logical sum, returned pending frame locations, and
host/nested-store ABI admission. Output pointers are validated without reading
their uninitialized payload. Rejected ready or pending writes leave the original
physical location unchanged. These checks also run in the default journey.

`--future-identity` selects the Linux process-gate journey. It checks all
optimization levels, a portable roundtrip and binding after source removal.
One copied future remains one item, independently evaluated pending
arithmetics remain distinct, and demanded control selects without changing
the chosen datum's identity. The compiler trace also checks that an unrelated
ready scalar function stays on the LLVM scalar and whole-SSA path.

## Transfer failure compatibility receipt

The frozen format-6 compiler accepted both programs below, and the frozen CLI
executed each with exit status 1, empty stdout and the original missing-file
transfer diagnostic:

```msync
fn fail() { ./missing -> ./destination }; fail()
```

```msync
fn fail() { ./missing -> ./destination };
if fail() { null } else { null }
```

Consuming the Bool does not erase an admitted transfer failure. The native VM
therefore counts a selected false transfer once, before `ExecuteAsync` returns;
disposal must not count it again. Pure computation futures that happen to yield
false are not transfers. The frozen source/byte receipt was generated under
`.temp/legacy-v6-import/transfer-failure-probe`; the source and required behavior
are reproduced here so cleaning disposable files does not lose the finding.

## Pending-range primitive acceptance

The runtime datum cursor and legacy vector ingestion are verified. Structural
native range primitives are being migrated and checked against frozen pending
payload journeys; complete pending-range builtin compatibility remains open
until those materialize/reverse/take/callback pipelines pass. Future handles
remain outside `NativeValue` throughout this work.

```sh
dotnet run --project tests/NativeExecution -c Release
```

Sources, instruction observations and actual file outputs stay under repository
`.temp/native-execution`. These are functional user journeys, not timing results.
