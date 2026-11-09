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

## November 9 paired component observations

The baseline is production `cd90173`; the candidate runtime implements
`68f6717`. The probe assembly is identical, and every case's serialized
executable bytecode hash agrees across the two sides. Both use .NET 10.0.14
Release on the same Linux host. `DOTNET_TieredCompilation=0` selects immediate
optimized managed compilation rather than allowing tier promotion to move
between observations; these are **managed component observations, not Native
AOT latency claims**. Three alternating process pairs each retain eleven
samples of 32 executions. Framework preparation and compilation are outside
the prepared VM timer.

| Case, size256 | p50 ms before / after | p95 ms before / after | Allocated bytes before / after |
| --- | ---: | ---: | ---: |
| Stable adjusted calls | 0.94669 / 0.86680 | 1.27011 / 1.20329 | 363,056 / 213,024 |
| Changing callable targets | 1.43300 / 1.32155 | 2.16063 / 1.99181 | 505,192 / 275,352 |
| Captured callables | 1.05625 / 0.72434 | 1.64447 / 1.15288 | 365,104 / 214,752 |
| Escaping Scope/closure factories | 11.01505 / 10.94532 | 12.80604 / 14.38503 | 1,326,400 / 1,055,752 |
| Bound Path loads plus observations | 9.25564 / 9.38572 | 11.09302 / 11.19810 | 925,840 / 819,544 |
| Literal control | 0.00350 / 0.00245 | 0.00731 / 0.00467 | 1,504 / 1,384 |

The factory/Path cases intentionally retain actual `exists` observations. Their
timing does not establish a speedup: the factory tail is worse in this sample,
and Path medians are essentially unchanged. Their allocation reductions are
deterministic. Stable/captured call allocation falls 41.3%/41.2%; the full change
includes the scalar lane's removal of per-instruction display classes, not just
owner and FunctionValue savings.

An isolated object-only source transform, excluding the scalar/operand-stack
lane, reduces stable calls from 363,104 to 315,880 bytes, captured calls from
365,152 to 317,928 bytes and factories from 1,328,448 to 1,266,944 bytes in the
original tiered run. Those observations attribute a useful part of the savings
to owner/argument/callable lifetimes; noisy tiered timing is not a latency claim.

Raw records and dependency SHA-256 identities are retained under
`.temp/object-allocations/combined-tier0/summary.json`,
`.temp/object-allocations/object-only-paired/summary.json` and
`.temp/object-allocations/object-only-tier0/summary.json`. The combined runtime
DLL hashes are baseline
`ae55772f8b7270867628281af6f56c667fe20be3dc222740a653ebbba37a46e9`
and candidate
`fbe5ae4f11c1b0aa0e4be7d364d76ae9d441d4cf05682b682ba48c44e85db0c6`.
Reproduce the managed comparison with the earlier command prefixed by
`DOTNET_TieredCompilation=0` and `--samples 11 --iterations 32`.
