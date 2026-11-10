# Generic instantiation journeys

This executable starts with real source, analyzes it into Typed HIR, writes and
reads `.mhir`, lowers the loaded semantic graph and executes it. It checks code
sharing independently from runtime results, including scalar/Bool arguments,
Scope results and writes, nullable field/capture effects, aliased inputs,
returned closures, fresh object field/capture identity, recursive type queries
and inferred signatures. Parameter Path/String conversion is checked both
directly and through a returned closure.

Annotated return checks preserve an initializing block and captured side effect
around a nonbreaking loop. Tail and explicit-return Path/String conversions,
including mixed branches, remain independent of the node-free result summary.

The joined-Scope case preserves a common member across two differently shaped
branches while sharing one Bool-specialized body. Its result depends on the
runtime branch, not retained binary-join construction history.

Fresh constructor results infer independent first-write field carriers while
sharing one code body. The closure-only named-Scope case checks the opposite:
returning only a setter cannot turn a singleton field into a fresh constraint.

Declaration inference checks distinguish a provisional `Range<ElementVar>`
shape from an explicit broad `Range` annotation. Inferred String fields reject
Number elements, explicit broad bindings permit them, and separate String/Bool
factory specializations preserve their own field carriers.

Run with an isolated artifacts directory:

```text
dotnet run --project tests/HirInstantiation -c Release --artifacts-path .temp/hir-instantiation-tests -m:1
```
