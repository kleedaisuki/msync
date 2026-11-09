# Typed HIR artifacts and inspection

## Boundary

`.mhir` persists the compiler's semantic program, not a disassembled instruction
stream or an AST accompanied by source to evaluate again. A loaded artifact is
an input to the same pure HIR-to-bytecode lowerer used after source analysis.
Deleting the original source must not prevent lowering, optimization, binding
or execution of a self-contained exported program.

The language keeps three distinct persisted stages:

| File | Meaning | Context |
| --- | --- | --- |
| `.mhir` | Typed semantic definitions, expressions and capture/location identities | Independent of bytecode slots and physical execution context |
| `.mso` | Relocatable typed bytecode | Execution context remains symbolic |
| `.mexe` | Context-bound executable bytecode | Retains the selected binding context, not live external observations |

The HIR container starts at format 1. Existing bytecode remains format 4.
Language semantics stay `0.8.0`; the product release is `0.8.1`. These versions
are separate contracts. Introducing HIR does not invalidate published bytecode
by changing its language version string merely to match the product version.

## User workflow

```text
msync compile sync.msync --emit hir -o sync.mhir
msync disassemble sync.mhir
msync disassemble sync.mhir --json
msync compile sync.mhir -o sync.mso
msync bind-ir sync.mhir -o sync.mexe
msync run sync.mhir
```

An output ending in `.mhir` selects HIR emission when `--emit` is not supplied.
Explicit `--emit bytecode` retains normal bytecode emission. Without `-o`, the
compile command prints the selected representation; `-o -` writes binary bytes
to stdout and keeps diagnostics on stderr. Reading an artifact for inspection
does not initialize runtime execution, bind paths, authenticate or perform
filesystem observations beyond reading that artifact.

## What survives serialization

Semantic arenas use stable module-local IDs rather than object addresses.
Sources, types, definitions, logical storage locations, function identities,
capture modes, structured nodes and control-flow/effect information retain
their explicit relationships. Field selection and calls must carry the
semantic information required by lowering, without looking names up again in
a mutable caller environment.

Finalized executable specializations are self-contained. Generic definitions
retained in the model must have semantic bodies and constraints, not hidden
syntax pointers; they are not an excuse to invoke the old binder after loading.
An export cannot silently omit a required function, shape, template or capture.

Source text, when retained for diagnostics, is data only. Locations refer to
source identities and spans; they are not raw offsets paired with whichever
source document happens to be current. Absence of the original file must not
cause source replay or erase semantic definitions.

Live cells, Process controls, pending tasks, credentials and authenticated
connections do not belong in an HIR artifact. A session export with unresolved
locations must declare its imports; it is not a serialized REPL heap. A
standalone lowering or execution request reports those required imports rather
than inventing values or replaying submission history.

## Representation and inspection

The binary codec uses explicit semantic tags and ID tables. It does not use a
general reflection serializer, encode CLR object identity, or embed an `.mso`
payload as the supposed high-level representation. Floating-point literal bits,
including signed zero and NaN payloads, remain exact. Structural validation
checks the file's actual references and invariants without arbitrary source or
node quotas.

The native inspection API keeps the existing artifact-view schema and adds the
optional `artifactKind: "hir"` discriminator. Existing bytecode models remain
compatible. HIR output describes semantic definitions, types, expressions,
locations and captures rather than pretending that bytecode slot numbers or an
optimization level are HIR properties. VS Code uses the same read-only native
view, source links and file-change notification ownership as existing IR files.

## Engineering basis

[MLIR bytecode](https://mlir.llvm.org/docs/BytecodeFormat/) separates container
versioning from the operations and types represented in it. That distinction is
useful here without introducing an MLIR runtime dependency.
[rustc serialization](https://rustc-dev-guide.rust-lang.org/serialization.html)
shows why arena references require an explicit reconstruction context rather
than serializing process addresses. msync uses its own module-local ID tables.

Native AOT favors an explicit codec and structured text writer. If JSON source
generation is used, its closed type set must be supplied explicitly; the
[System.Text.Json source-generation contract](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
does not make arbitrary object-valued graphs statically serializable.
