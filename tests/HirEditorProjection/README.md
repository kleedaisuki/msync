# Immutable HIR editor projections

This executable links only the HIR/Core references and the editor view/index/
presenter source. It contains no semantic compiler, bytecode lowerer, mutable
`Info`/`Frame` graph or runtime evaluator. Hand-built semantic generations make
the presentation boundary independently exercisable while the new binder is
being integrated.

The focused checks cover named versus anonymous Scopes, parameter role override,
function and native-alias signatures, open generic/error descriptors, nullable
fields, nested common-member intersections across concrete generic variants,
recursive value/type schemas, physical Path provenance, lexical owner boundaries
and source-owned UTF-16 definition ranges. Native metadata is requested only for
the selected value and is cached; supplied semantic documentation does not cause
another source lookup.

The presenter intersects typed field references before formatting, so nested
union types retain their actual type union and their common capabilities.
Expansion terminates at repeated value/type identities or a repeated variant
combination, not at an arbitrary depth. Scalar descriptions do not allocate
member dictionaries or recursive-expansion sets.

```sh
dotnet run --project tests/HirEditorProjection -c Release \
  --artifacts-path .temp/hir-editor-projection/build
```

For a test-only build against an owner's frozen matching Core/HIR DLL pair,
set `-p:HirReferencePath=/absolute/reference/directory`; this avoids rebuilding
the shared product during parallel work. The initial isolated run passed 37
contracts with zero warnings. The subsequent four retention checks keep the
editor view alive while verifying that the original module and its 8,192-node
arena can be reclaimed; generic result types remain available after their bodies
are released. The total is 41 contracts. Dependency hashes are recorded under
`.temp/hir-editor-projection/reference/SHA256SUMS`.

`tests/Msync.EndToEnd/hir_editor_journey.py` separately checks the real LSP
protocol. Semantic architecture is established here; the external journey
checks that editor behavior survives its integration into the native host.
