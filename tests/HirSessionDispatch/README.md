# Open-session callable journeys

This is a real `CompilerSession` / `ExecutionSession` journey. Each accepted
submission is executed and reconciled using actual exported locations. The tests
do not construct bytecode modules to stand in for the source compiler.

The three optimization levels cover deferred calls across new function
definitions, capture storage, independent Number/String signatures, Scope aliases,
assignment rejection and compilation rollback, new target effects, native
callback adaptation, partial execution, and shared/private factory cells.

Build the test host against a coordinator-owned frozen producer to avoid
rebuilding shared product projects:

```sh
dotnet run --project tests/HirSessionDispatch -c Release \
  -p:FrozenAssemblies=/absolute/frozen/producer \
  --artifacts-path .temp/hir-session-dispatch/tests
```

`--basic`, `--before-effects` and `--before-factories` isolate the corresponding
integration prefix while an interface is being introduced. They are not
substitutes for the complete final journey. The frozen9 producer passed the
33-check pre-factory prefix at O0/O2/O3; frozen11 passed the 15 affected factory,
root-replacement and shadowing checks with `--factories-only`. `--trace` prints
the actual compiled dispatch tables when investigating a new binding path.
Runtime-only slot mechanics and optimizer
opacity have their own smaller tests under `SessionDispatchRuntime` and
`SessionDispatchOptimization`.
