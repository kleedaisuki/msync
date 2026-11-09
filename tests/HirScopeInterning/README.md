# Structural Scope layout interning

`dotnet run --project tests/HirScopeInterning -c Release` checks identity and
rollback behavior against linear structural equality, including rows appended
directly by binding and a reused table position after rollback.

The optional `--benchmark` runs the old linear search and current indexed lookup
against the same two-field rows. It reports cold indexing and steady allocation
separately; elapsed times are observations, not test thresholds. For controlled
managed comparisons, set `DOTNET_TieredCompilation=0` before running the Release
host. The compiler architecture note records the setup and a real List compile
comparison; the microbenchmark is not a claim about overall program speed.
