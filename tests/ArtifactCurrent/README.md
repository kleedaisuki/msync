# Current artifact execution contract

```sh
dotnet run --project tests/ArtifactCurrent/Msync.ArtifactCurrent.Tests.csproj
```

This direct Core/Runtime journey round-trips the current format and captured path
projection, rejects formats 1/2/3 with recompilation guidance, and rejects obsolete
execution modes. It serializes bound executable transfer operations, checks actual
output bytes, then removes the selected input and executes the same artifact
again. This distinguishes a frozen execution context from a stale filesystem
existence proof. Temporary input/output belongs to the journey under `.temp`.
