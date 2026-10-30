# Typed objects and lexical context in 0.5

The object format contains expanded typed instructions, directional barriers,
explicit process awaits, symbolic context relocations, and owned diagnostic
source maps. It does not contain filesystem observations, snapshots, open
handles, capability certificates, transaction state, or serialized source ASTs.
The `MSYNC-OBJECT` envelope is version 2 and requires language version `0.5`;
version 1 is rejected rather than interpreted with different semantics.

`context::contextualize` is lexical module instantiation, not a dynamic
filesystem linker. It validates an object, substitutes `$pwd`, module-file,
home, and environment anchors, assigns fresh process identities, and installs
the typed exports atomically into a candidate compiler session. Explicit
`Context.environment` and `Context.home` overrides win over the invoking
process's environment. Their values are captured at this instantiation; a
captured value remains fixed, while an exported namespace remains a namespace
and follows a later submission's context.

Neither instantiation nor object validation checks whether a bound path or cwd
exists. `$path` executable searches, foreign home expansion, endpoint opening,
and program launch remain interpreter obligations. Relative guest paths stay
relative unless `Context.endpoint_cwds` explicitly supplies a guest absolute
base; the host working directory is never guessed into a `/mnt` mapping. The
interpreter evaluates only its current basic block, after the previous block
finishes. Thus a preceding program can create or delete a file without an
earlier object load making the later block stale.

`serde` and `serde_json` own the data codec. All schema containers reject unknown
fields. Validation additionally checks feature/version compatibility, bounded
operation identities, prior process definitions for ports and awaits, unique
stream consumers, valid endpoint types, UTF-8 source intervals, exact operation
origin coverage, and consistency of derived import/symbol tables. A relocated
operand is serialized as an empty placeholder and must have exactly one valid
relocation: deleting a relocation cannot reveal the compiler host's old absolute
path. Debug source is diagnostic data only; mutating valid typed literal IR is
required to change execution even if the debug text still says something else.

Object overwrite constraints are hints derived separately inside
`Region::blocks()`. They never authorize crossing a barrier, acquiring a future
block early, or replacing an execution-time guard with an old observation.

Relevant tests are `object_codec_v4`, `object_linker_v4` (historical target name),
and `independent_object_integrity_v4`. They include future nonexistent paths,
missing executables, external creation/deletion before a later block, fresh
Spawn/Await IDs on repeated instantiation, and untrusted schema mutations.
These are acceptance specifications until the parent records the actual
integrated build and execution results; source readiness alone is not a pass.

The second unified selected build produced the executable mapping
`.temp/barrier-second-executables.json`. Direct sequential execution of that
generation passed `object_codec_v4` (6 tests), `object_linker_v4` (9 tests), and
`independent_object_integrity_v4` (12 tests), including the real external-program
creation/deletion journeys. Logs are `.temp/barrier-artifact-<target>.log` and
the exit-code summary is `.temp/barrier-artifact-results.json`. This validates
those compiled paths, not subsequent edits or whole-project release readiness.

## Expanded source modules in 0.5.2

`include`, `execute`, and pure symbol calls disappear during lowering. Expanded
instructions still need the semantic `$file` context of their definition, even
when the source files no longer exist. Objects therefore optionally carry
`source_modules`, guarded by the `source_modules_v1` feature. Objects without
these bindings retain their existing encoding and binding contract.

Each entry associates an opaque module name with either an explicit absolute
source path or a contextual path expression. Contextual paths may depend on the
entry module or another source module. Validation requires unique non-main
names, a closed acyclic dependency graph, valid path operands, and at most 1024
entries. Only referenced bindings and their transitive parents are retained.
Unknown features remain errors for readers that do not implement them.

Instantiation evaluates this graph without opening source files. Explicit
`Context.modules` bindings override defaults. Diagnostic filenames and source
text are never used to invent missing module bindings; changing debug text must
not change execution. Source acquisition happens only when compiling source,
not while instantiating an object or replaying structured IR.

## Upstream design references

- [Serde container attributes](https://serde.rs/container-attrs.html): unknown
  JSON fields are otherwise ignored. Explicit rejection matters for executable
  objects, where silently ignoring an old transaction or a new barrier would
  change effects rather than merely omit display metadata.
- [MLIR bytecode format](https://mlir.llvm.org/docs/BytecodeFormat/): container
  versions and operation/dialect semantics are distinct concerns. The same
  envelope magic does not justify interpreting old transaction-bearing IR as
  barrier IR. This project deliberately rejects that semantic upgrade; adopting
  MLIR or writing a custom binary codec is unnecessary for its current workload.
