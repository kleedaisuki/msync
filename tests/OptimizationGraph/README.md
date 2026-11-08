# Optimizer differential acceptance

Run `dotnet run --project tests/OptimizationGraph -- --semantic` for selected
value/control-flow, callable/capture/recursion, failure and physical-byte
comparisons. Each original level-0 module is compared with levels 1–3 through
both relocatable and executable artifact serialization. Generated arithmetic
also uses an independent numeric oracle. Each transfer execution starts without
its output files; output from a previous candidate cannot conceal a lost effect.

Run without flags to include structural executable elimination contracts: unused
scalar, Scope, function and constructor work leaves only the null return, with
no unreachable function or constant table entries.

Run with `--mutable` for mutable binding/alias/capture/loop semantics and dead
nonescaping object-store elimination. These source contracts require the mutable
language implementation, including explicit nullable Scope fields.

The filesystem test binds and serializes while a path is absent, then creates
and deletes it between executions. Executable contextualization may freeze
path resolution, not the filesystem observation.

Invalid-member and invalid-regex modules intentionally discard the resulting
value: the selected operation must still produce its diagnostic. Numeric
zero division follows the runtime's current IEEE-754 result, not a fabricated
integer division exception.

Value-numbering checks retain runtime-unknown inputs from actual filesystem and
environment reads. Repeated Bool negation, numeric negation and String concat
must leave one residual operation; intervening object/captured-cell writes and
filesystem publication must invalidate stale facts. Raw-cell alias bytecode,
escaped field storage, legacy empty-stack returns and the distinct signs of
IEEE-754 zero are compared with explicit level-0 execution and independent
expected results.

Run `--directional` to exhaustively compare Before/After transfer placement for
all event sequences of lengths 2–7. Each moved transfer must remain inside its
original legal marker interval. An independent dynamic program computes the
minimum number of nondecreasing transfer groups; source-owned marker/transfer
identities must remain ordered. Paired admission identities and legacy module
semantics remain opaque. Four byte-level differential cases also cover repeated
publication and read-after-write across mixed directional markers.

## Linux Native AOT integration evidence

After directional normalization, an isolated Native AOT publish passed five
actual process journeys (`owner`, `overlap`, `resource-order`, `selected`,
`deferred-dataflow`) in both source and serialized bound-executable modes:

```text
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 \
  --artifacts-path artifacts/build/optimizer-directional \
  -o artifacts/validation/optimizer-directional-linux
python3 tests/Msync.EndToEnd/scheduling_journey.py \
  --binary artifacts/validation/optimizer-directional-linux/msync \
  --case owner --case overlap --case resource-order \
  --case selected --case deferred-dataflow --executable
```

The second invocation omitted `--executable` for the source path. The fixture
interpreter was `/home/moesegfault/.python/bin/python3`. Executable SHA-256:
`264b4d14595f5a831371ab8b5399171d3670caa1b82170b2124e2b1089b7380e`.
The tested `DirectionalNormalization.cs` SHA-256 was
`42b086395fe4472e70f6cd92ffc39f33dca29f0560db0c0d54670a895e2d1533`.

Separate artifacts output avoids concurrent publishers replacing the DLL/PDB
pair while ILCompiler reads sequence points. The first shared-output attempt
failed in that reader with `Invalid compressed integer`; the isolated build
completed without changing product code or disabling debug information.
