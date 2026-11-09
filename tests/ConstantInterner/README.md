# Constant-pool indexing and literal transfer scripts

Run `dotnet run --project tests/ConstantInterner`.

The oracle checks stable existing indices, append synchronization, bit-exact
signed zero and NaN payloads, and refusal to merge mutable allocations or
closures with mutable captures. It also optimizes an ordinary 1,024-transfer
bytecode function and asserts that every original constant operand is retained
and the interning dictionary is never initialized. This is an algorithmic
regression check, not an elapsed-time threshold or an I/O simulation.

Actual byte transfers, selected runtime errors and artifact round trips remain
covered by `tests/OptimizationGraph`; Native AOT optimizer-phase measurements
are owned by `tests/Performance/Components`.
