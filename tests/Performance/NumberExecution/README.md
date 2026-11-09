# Number execution and allocation probe

This executable compiles real source at O0/O2/O3 and checks source, serialized
relocatable bytecode and source-free executable bytecode. Measured O2 programs
retain dynamic loops, arithmetic, comparisons and captured mutation: each report
records the bound artifact SHA256 and residual numeric instruction count. It
also checks 1,500 binary64 arithmetic/codec cases bit-for-bit, including negative
zero, subnormals, infinities and distinct NaN payloads, plus duplication/discard.

```sh
dotnet run -c Release --project tests/Performance/NumberExecution -- \
  --cases arithmetic,powers,captured,literal --size 1024 \
  --samples 15 --iterations 16 --output .temp/number-runtime/current.json
```

Native AOT is enabled for publication. `--check` skips measurements, retaining
semantic/codec checks. All outputs default below `.temp/number-runtime`; the
probe does not perform transfers or start external processes. Object/IO controls
belong to the adjacent ObjectAllocations probe and the end-to-end journey.

A frozen pre-change set of Msync.Core/Compiler/Runtime assemblies may be supplied
with `-p:FrozenAssemblies=/absolute/directory` for a matched before/after probe.
Use a separate `NuGetLockFilePath` for that mode because direct assembly
references intentionally have a different dependency graph from product project
references. Ordinary and both Native RID project-reference lockfiles are checked
in. Use isolated `--artifacts-path` directories when collaborating.

For managed comparisons set `DOTNET_TieredCompilation=0` identically on both
processes; otherwise the first measured scenario can include tiered compilation
of the large interpreter dispatch loop. Native AOT does not have that phase.
Compare alternating before/after processes with identical artifact hashes and
parameters. Values retained in local slots, calls, fields and Ranges remain boxed
at public Value boundaries: the raw-double operand stack removes arithmetic
intermediates, not the entire object representation.
