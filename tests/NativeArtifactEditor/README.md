# Native artifact editor contract

From the repository root:

```text
dotnet run --project tests/NativeArtifactEditor
dotnet run --project tests/NativeArtifactEditor -- produce .temp/native-editor-plan.mexe
python3 tests/Msync.EndToEnd/artifact_view_journey.py --binary src/Msync.Cli/bin/Debug/net10.0/msync --plan-artifact .temp/native-editor-plan.mexe
```

The small executable links the production renderer, rather than reproducing its
logic. It checks native prepared plans and accepted format-6 artifacts with exact
UTF-16 symbol/reference/hover positions, ordered selected plan results, dependency
edges, malformed input diagnostics, and cancelled inspection. No historical
runtime values or modules are constructed by the rendering route.

The Python journey uses the actual LSP process. It removes the source before
inspection, checks artifact hashes and absence of transfer effects, opens virtual
URIs, rejects command-URI debug-link injection, cancels a large in-flight view,
and verifies subsequent source hovers and artifact requests still work. The
optional prepared fixture contains two transfers, snapshot input, predecessor
edges, concrete read/write resources, two batches and ordered selected results.
