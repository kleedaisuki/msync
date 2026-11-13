# Native call/allocation diagnostics

These are explicit mechanism diagnostics, not default general applications.
They now execute `NativeProgram` through the one native VM; old borrowed-frame,
number-boxing and Scope object mechanisms are retired. The original source
call/capture/reassignment/Path/order/error/owner/session oracles remain.
`--check` runs O0/O2/O3 source and detached relocatable/executable routes;
`--check-source-order` independently checks the callee is captured before an
argument changes its binding. Handwritten native IR checks source dispatch
cannot override an evaluated native descriptor and distinct empty environments
remain distinct Range members.

```sh
dotnet run --project tests/Performance/ObjectAllocations -c Release -- --check
dotnet publish tests/Performance/ObjectAllocations -c Release -p:RuntimeIdentifier=linux-x64 \
  --artifacts-path artifacts/object-probe-build -o artifacts/object-probe
artifacts/object-probe/ObjectAllocations --case captures --size 256 \
  --samples 15 --iterations 8 --output .temp/object-probe/captures.json
```

`PrepareMilliseconds` includes compile, contextual preparation, encode and
strict decode. `FirstRunMilliseconds` includes fresh VM setup, execution and
cleanup. Repeated samples also use fresh interpreters; they are not isolated
steady-state dispatch times. Allocated bytes are separate managed allocation
traffic, not live heap/RSS or CPU time. Wall samples include all awaited work.
`MappedCalls`/`DirectCalls` count actual finite dispatch symbols/static source
targets in native IR, not old execution tiers. `literal` is a nonhit control;
`io` is an explicitly warm fixed-payload overwrite diagnostic, not a storage
bandwidth score or the maintained anti-noop transfer benchmark.

The source generator and executable hashes are recorded separately. The paired
`compare.py` enforces identical source hashes, retains both artifact hashes and
reports whether byte identity holds: different compiler outputs are not silently
called a same-artifact runtime comparison. For a runtime-only causal comparison
require that reported identity yourself; for pipeline changes use the independent
result oracle and disclose both producer versions. Keep guest `MSYNC_JIT` policy
and toolchains matched; ordinary managed runs establish correctness only.
