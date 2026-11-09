# Persistent semantic-session boundary

This focused executable exercises the actual compiler/session and interpreter,
not a mock REPL. It fixes the consumer contract while semantic analysis moves to
Typed HIR: repeated editor analysis must not publish declarations, invoke a
captured function or perform file effects. Accepted execution must still retain
Scope aliases, definition-time `global` locations and late Range reads.
Incomplete member access must retain the accepted receiver shape without
reserving unfinished declarations in the persistent namespace.

The same session crosses successful compilation/execution, failed analysis,
partial runtime failure and a Unicode/spaced working-directory change. Binding
values remain mutable across submissions, including an explicitly nullable
Range. Runtime failure retains an initialized declaration prefix but does not
publish a declaration whose initializer failed.

The public session facade currently supplies the executable image and runtime
feedback callbacks. The Typed HIR implementation must translate those callbacks
through its semantic-location/lowered-slot map; bytecode offsets are not semantic
declaration identities. Existing terminal/PTY fixtures separately exercise the
human prompt, live Processes and Ctrl+C instead of repeating them here.

```text
dotnet run --project tests/HirReplSession -c Release
```

For an immutable owner-produced product snapshot, pass
`-p:FrozenAssemblies=/absolute/path/to/snapshot` and use an isolated artifacts
directory. The probe supports O0/O2/O3 within one invocation. A passing result on
the 0.8.0 baseline establishes behavior only; it does not prove that the new
semantic implementation has stopped emitting bytecode during analysis.

## Accepted baseline

The frozen five-project 0.8.0 managed Release snapshot passed **75 contracts**.
Only this test executable was built. Its Compiler SHA-256 is
`8c4cd638db974d29f60674804536d245af629554a74228df9201014072e3dc2f`;
Runtime SHA-256 is
`ca301b49b73d641711eec6ee5d65ec55b87692620a0de9eda6f711bcfb5ab6fd`.
Build/restore output is disposable under `.temp/hir-repl-session-baseline/`.
The same source will distinguish observable regressions after the new semantic
and lowering boundaries are connected.

## Typed HIR bridge acceptance

The integrated bridge passed **105 contracts** at O0/O2/O3 against the
immutable managed product snapshot with Compiler SHA-256
`3296ff6984adfbb10360c836568b90c9ac7c24bd48b503cdf5b87385d3592b42`.
Additional journeys retain generic definitions across later argument types,
reject a bad specialization without publishing its declaration prefix, and
execute a returned private recursive closure in another submission. A live
runtime Scope update is visible through an explicit editor feedback resolver,
while that query leaves accepted semantic facts unchanged. Distinct named Scopes
retain their own singleton allocations across generations even when sealing
compacts their construction tables; the runtime receipt uses stable allocation
site identities rather than dense construction indices.

The bridge itself no longer owns an emission-oriented `Compilation` instance.
Analysis produces a temporary semantic generation and a read-only observation
projection without invoking the bytecode lowerer. This managed acceptance does
not replace the final Native AOT, actual terminal and installed editor journeys.

## Focused optimization and specialization reuse

Run `dotnet run --project tests/HirReplSession -c Release -- --optimization`
for the bounded session-cost journey instead of the complete session suite.
It checks optimized typed function bodies, repeated specialization reuse,
rejected-generation cache rollback, a second argument type, shared captured
Scope state and partial runtime failure at O0/O2/O3.

The frozen managed candidate with Compiler SHA-256
`1dcbaba83cf2b92fd144a8c813c224f2a2f87784f44efcf490a7c30a658ba5c2`
passed this journey. `value + 2 * 3` retained six typed-function bytecode
instructions at O0 and four at O2/O3, with the same result. Across eight repeated
calls, typed function count grew by seven before the session cache and by zero
afterward. The session entry itself remains unoptimized under the existing
`PreserveLocals` contract; this probe intentionally measures a non-entry body.
