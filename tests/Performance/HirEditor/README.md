# Semantic editor component scaling

This probe compares frozen product assemblies through the unchanged public
`EditorCompiler.Analyze` and `EditorAnalysis.At` APIs. It measures an incomplete,
uncalled generic body after 0, 80 or 400 ordinary Path declarations, without
standard-library initialization, filesystem enumeration or bytecode execution.
It checks that the body parameter and known receiver member remain usable.

Build one isolated host per assembly set, with `FrozenAssemblies` pointing to
immutable DLLs and a distinct `--artifacts-path`. Reference resolution uses only
explicit HintPaths; MSBuild must not pick an earlier local product DLL.

Run each host with `DOTNET_TieredCompilation=0` on both sides and arguments
`BINDINGS SAMPLES`. Reports separate cold latency/allocation from three warmups
and the warm samples, and record the actually loaded Compiler SHA256. Alternate
baseline/candidate runs on one host outside builds. These are managed component
observations, not Native AOT or full LSP latency claims, and not CI thresholds.

## Checked language-server revisions

`revisions.py` compares two actual stdio language servers with fresh processes,
alternates their order, and verifies the current Number/String hover on every
edit. It records opening, cosmetic, first body and consecutive body latencies
separately. Use `--fixture generic` for independent generic declarations and
`--fixture seeded-set --rows 16` or `--fixture seeded-list --rows 16` to add
ordinary standard-library calls shared by both 0.8.5 and later releases. The
library controls exercise semantic reanalysis even where prefix reuse cannot
apply. Run without concurrent builds; retain the raw samples and executable
hashes rather than treating the medians as latency gates or tail estimates.
