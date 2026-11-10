# Current artifact execution contract

```sh
dotnet run --project tests/ArtifactCurrent/Msync.ArtifactCurrent.Tests.csproj
```

This direct Core/Runtime journey round-trips the current format and captured path
projection, rejects formats 1/2/3/4 with recompilation guidance, and rejects
obsolete execution modes. Format 5 preserves contextual transfer kinds, resource
footprints, dependencies, batches, observed results and source origins. Its codec
rejects a cyclic predecessor reference during intake; the VM has no environment
freshness check.

Scalar and cohort Bool outputs share the same transfer-index vector. Null means
unobserved; an empty vector is the successful empty cohort. Explicit finite
Range snapshots persist their expanded members. Retained inputs of a lazy Range
are not a frozen selection and cannot be serialized as if they were its outputs.
Resource footprints are vectors for every operation: literal writes have no
reads, ordinary copies have one source, and replacement cohorts or directory
alias projections retain every relevant root without a primary/extra split.
Source-free directory creation or cohort clearing uses Null only for the
`EnsureDirectory` operation, rather than a synthetic pathname; file and metadata
operations still require their actual Path source.

The journey also serializes bound executable transfer operations, checks actual
output bytes, then removes the selected input and executes the same artifact
again. That submission reports the concrete missing-file operation rather than
classifying the plan as stale or rebuilding it. Temporary input/output belongs
to the journey under `.temp`.
