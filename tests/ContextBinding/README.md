# Contextual native preparation

Run `dotnet run --project tests/ContextBinding -c Release`.

Real source and strict native artifact roundtrips exercise captured origin,
no re-preparation of executable artifacts, selected and unselected transfers,
missing-source diagnostics, unknown nested loop effects and canonical path reads
after symlink publication (Linux). Preparation must not execute transfer bytes.
An executable residual image is also launched from a different working directory
to distinguish runtime origin from the frozen binding context.

`--benchmark` measures native preparation separately from compilation. The old
stack-block rewrite counts and local-transfer fast-target counters were retired;
no equivalent speedup is inferred from those historical measurements.
