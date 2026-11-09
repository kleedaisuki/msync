# Semantic HIR boundary witnesses

This executable calls `Compiler.Analyze`, never the old `Compiler.Compile`
facade. It inspects the actual semantic module, lowers it twice, serializes and
reads the HIR, lowers the loaded representation and executes the resulting
bytecode. It distinguishes fixed Null admission from the narrower type of a
guarded value read. A runtime-dependent `exists` condition retains an `If` node;
analysis and lowering do not evaluate the filesystem operation.

The first coherent semantic snapshot accepted **38 assertions** over five small
sources: arithmetic, assignment, nullable refinement, a subsequent admitted Null
write, and a runtime branch. Build completed with zero warnings and errors.
The exact immutable producer is `.temp/hir-editor-cli/frozen/` with
`snapshot-receipt.json`; Compiler SHA-256 is
`1f6af0107fecce36009bec27d2d00069aa0472a7df9f2924b0356b6c65a52de7`
and HIR SHA-256 is
`85875220b77cd104428b5d4cee63d916b3c4b359ae8fd0e81e87af0151863d0b`.

The preceding snapshot exposed a real storage-domain defect: a declaration
admitted Null, but assignment checked only its non-null annotation. The fixed
snapshot accepts that write while preserving a non-null guarded read. The test
does not require redundant encoding of nullable information in both the type
table and `HirLocation.AdmitsNull`; either model is valid only if the effective
domain remains correct.

```sh
dotnet build tests/HirSemanticBoundary/Msync.HirSemanticBoundary.Tests.csproj \
  -c Release -p:FrozenAssemblies=/absolute/immutable/product/assemblies \
  --artifacts-path .temp/hir-semantic-boundary/build \
  -o .temp/hir-semantic-boundary/run
.temp/hir-semantic-boundary/run/Msync.HirSemanticBoundary.Tests
```

Without `FrozenAssemblies`, the project uses ordinary project references. Pass
case names to run a changed contract alone. Frozen-assembly builds compile only
the test and do not race the shared product producer.

These small witnesses prove the new path is usable; they do not prove full
frontend migration, old-mechanism deletion, generic/query purity, editor
retention or published-product acceptance. Those remain separate requirements
in [the validation contract](../../docs/design/typed-hir-validation-v081.md).
