# Mutable Scope scalar replacement

Run `dotnet run --project tests/ScopeScalarization`.

The executable compares selected runtime results before and after field-memory
versioning and scalar replacement. It exercises aliases, repeated stores,
escaping returned objects, closure-shared field cells, calls, control-flow
boundaries, rebinding, artifact serialization and filesystem changes between
executions. The direct pass invocation isolates its proof obligations from
constant propagation and dead-code elimination in the surrounding pipeline.

The current scalar replacement region is one basic block. CFG live-out binding
analysis retains instances that cross region boundaries. Within the region,
fields retain independent scalar slots; writes through an alias update the same
field slot, and raw captured cells keep their original shared identity.
The raw-cell discriminators include `GetCapture` extraction, aliasing through
another local, and using a captured location as a Scope field. These execute at
optimization level zero so automatic executable binding cannot mask pass bugs.

`--performance` additionally measures 100/500/2000 independent mutable
instances after three warm-ups, reporting medians of nine Release executions.
The optional probe times the pass, not parsing, compilation, startup or I/O.
On the development Linux host with tiered compilation disabled, those medians
were 1.259/3.111/32.925 ms and 0.399/1.871/7.703 MB allocated. Allocation grows
with program size; the large case includes managed allocation/collection costs
and is not evidence of proportional latency or Native AOT performance.

The generated alias oracle also executes 24 reproducible 64-step programs with
rebindings and interleaved writes/reads. Its expected result comes from an
independent pair of scalar cells, not the unoptimized interpreter alone.
