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
