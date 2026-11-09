# Generic instantiation journeys

This executable starts with real source, analyzes it into Typed HIR, writes and
reads `.mhir`, lowers the loaded semantic graph and executes it. It checks code
sharing independently from runtime results, including scalar/Bool arguments,
Scope results and writes, nullable field/capture effects, aliased inputs,
returned closures, fresh object field/capture identity, recursive type queries
and inferred signatures. Parameter Path/String conversion is checked both
directly and through a returned closure.

Run with an isolated artifacts directory:

```text
dotnet run --project tests/HirInstantiation -c Release --artifacts-path .temp/hir-instantiation-tests -m:1
```
