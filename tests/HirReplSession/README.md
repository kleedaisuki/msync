# Persistent semantic-session boundary

This focused executable exercises the actual compiler/session and interpreter,
not a mock REPL. It fixes the consumer contract while semantic analysis moves to
Typed HIR: repeated editor analysis must not publish declarations, invoke a
captured function or perform file effects. Accepted execution must still retain
Scope aliases, definition-time `global` locations and late Range reads.

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

The frozen five-project 0.8.0 managed Release snapshot passed **69 contracts**.
Only this test executable was built. Its Compiler SHA-256 is
`8c4cd638db974d29f60674804536d245af629554a74228df9201014072e3dc2f`;
Runtime SHA-256 is
`ca301b49b73d641711eec6ee5d65ec55b87692620a0de9eda6f711bcfb5ab6fd`.
Build/restore output is disposable under `.temp/hir-repl-session-baseline/`.
The same source will distinguish observable regressions after the new semantic
and lowering boundaries are connected.
