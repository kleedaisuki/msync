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
