# Text and regex journeys

Run from the repository root:

```
dotnet run --project tests/StdlibTextRegex/Msync.StdlibTextRegex.Tests.csproj
```

The executable checks source, relocatable binary round-trip and bound executable
round-trip at O0/O2/O3. It exercises Unicode scalar Match/Group rows, optional
groups, pattern binding, replayed record identity, builder aliasing/freshness,
clear/reuse, text snapshots and balanced-tree chunk order beyond power-of-two
boundaries.

During shared product compilation, build only the harness with
`-p:BuildProjectReferences=false`, then run the resulting DLL. `--source-library`
loads current `stdlib/text.msync` directly instead of the compiler assembly's
embedded snapshot; normal mode validates actual embedded product bytes.
`--bench` excludes compilation, warms once and reports three-sample medians of
prepared interpreter execution and total managed allocations for 64/512/4096
32-character appends. It validates output length; results are not Native AOT
startup or IO measurements.
`--bench-source=PATH` selects a source baseline; the accepted source-forest
comparison and its reconstruction are recorded in
`docs/design/text-regex-library-v080.md`.
