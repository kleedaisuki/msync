# Tail-call frame reuse

Run `dotnet run --project tests/TailCallOptimization`.

The differential journey compiles ordinary source with optimization disabled,
applies the tail-call pass, validates and serializes the bytecode, and executes
both representations. It checks argument swapping, shared captured writes,
fresh locations retained by escaped closures, pass idempotence and a 100,000-step
recursive computation without recursive interpreter frames. Functions which own
scheduled work retain their call/return lifetime boundary.

The transformation stages arguments and the actual callee closure before
replacing frame slots. Captures are extracted from that closure, not copied
from the current invocation. Local slots are replaced with fresh locations so
previously escaped cells retain their original invocation's payload.
