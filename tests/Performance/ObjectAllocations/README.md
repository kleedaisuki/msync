# Object allocation and invocation ownership

This probe consumes actual prepared msync programs, not an isolated BCL loop.
It extends the earlier independent adjusted-dispatch experiment with fresh
Scope/closure factories, bound Path constants, and nested transfer owners.

```text
dotnet run --project tests/Performance/ObjectAllocations -c Release \
  -p:PublishAot=false -- --check
dotnet run --project tests/Performance/ObjectAllocations -c Release \
  -p:PublishAot=false -- --check-source-order
```

`--check` executes direct/changing/captured callables, object methods, Paths and
the literal control through source, serialized `.mso`, and serialized `.mexe`
at O0/O2/O3. Real nested transfers plus release/acquire check that completed
inner invocations do not discard their caller's task ownership. Additional raw
IR cases retain actual native callee identity, invalid/missing specialization
diagnostics and distinct zero-capture closure equality. A persistent session
checks that old deferred calls retain their origin while observing a replaced
callee and that new captured Paths retain their own context.

The production change represents a call owner as three frame-owned references,
not a separately allocated identity object. It is copied into the suspended
caller frame; the current frame is joined before that saved caller is restored.
No task captures the owner. There is no owner pool, global resource registry,
effect-driven second interpreter or removal of return joins. A genuinely async
frame may grow when references become a value struct, so measure net allocated
bytes rather than claiming every call necessarily saves a fixed heap amount.

Selected specialization targets are integers passed with the original callable;
its captures and native identity do not need a replacement FunctionValue.
Unchanged adapted callable targets can reuse their value. Empty argument
vectors are shared, but MakeClosure deliberately retains fresh empty capture
storage: FunctionValue's established equality includes that storage identity.
Paths whose working/home directories are already captured are immutable and do
not need an identical replacement on every constant load.

For matched images, freeze product sources before editing and build this same
project with `-p:MsyncSourceRoot=ABSOLUTE_FROZEN_ROOT` and an isolated
`-p:ArtifactsPath=...`. The project references real product projects; it does
not certify private DLL paths as deployment dependencies. Linux/Windows RID
lock files support the same project under Native AOT.

```text
python3 tests/Performance/ObjectAllocations/compare.py \
  --control CONTROL_PROBE --candidate CANDIDATE_PROBE --output RESULTS
```

`compare.py` accepts Native executables or managed `.dll` probes, alternates
AB/BA observations, records actual runtime/compiler dependency hashes for the
latter, and checks the compiled executable artifact hashes agree. The default
six cases use size256, three pairs, nine timed observations per process and
16 executions per observation. Preparation, first run, complete process wall
time and prepared execution are distinct; only the last is paired in the
summary. Allocation is total runtime traffic, not retained bytes or RSS.

The platform engineering context is [.NET's allocation/closure discussion](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
and [CoreCLR's object-stack-allocation design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md).
The corresponding research principle is to preserve only observable escaped
identity, as in [Partial Escape Analysis and Scalar Replacement](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf).
These sources motivate lifetime/representation analysis, not an assumption that
JIT-only optimizations automatically apply to Native AOT or msync bytecode.
