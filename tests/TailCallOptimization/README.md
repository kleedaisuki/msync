# Tail-call frame reuse

Run `dotnet run --project tests/TailCallOptimization`.

The differential journey compiles ordinary source with optimization disabled,
applies the tail-call pass and the integrated O1/O2/O3 optimizer, validates and
serializes the bytecode, and executes both representations. It checks argument
swapping, shared captured writes,
fresh locations retained by escaped closures, pass idempotence and a 100,000-step
recursive computation without recursive interpreter frames. Functions which own
scheduled work retain their call/return lifetime boundary.

The transformation stages arguments and the actual callee closure before
replacing frame slots. Captures are extracted from that closure, not copied
from the current invocation. Local slots are replaced with fresh locations so
previously escaped cells retain their original invocation's payload.

Specialization indices do not erase a runtime native function identity. A call
therefore needs a known non-native closure producer, or a closed module in which
native callable values cannot be constructed: no native function constants,
native builtin scopes, injected session locations, or externally supplied lazy
Range/cell constants. Ordinary standalone recursive functions satisfy the latter
proof even though the recursive binding itself is a captured cell.

Capture-count bounds also preserve the old direct-specialization behavior: an
actual closure larger than the target frame raises its original frame-copy
error rather than becoming a tail loop. The test constructs and validates that
artifact and compares its O0, isolated-pass and integrated O3 failures.
