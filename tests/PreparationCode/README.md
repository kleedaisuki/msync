# Contextual preparation code emission

This focused source-link test exercises the actual internal `PreparationCode`
helper without widening product visibility or rebuilding the product. Its oracle
is the pre-extraction emitter from `4199e9d`; 1,500 deterministic generated cases
and explicit fixtures compare instruction values (including operands, names and
source spans), original-to-output jump targets and output-to-original provenance.

The fixtures cover empty code, removed Nops, empty/multiple replacements,
collapsing target positions, end-PC targets, all three ordinary branch kinds and
`IteratorNext`'s distinct local-slot/target operands. Generated branch targets
stay within the original function's valid PC domain, including its end sentinel.
These are emission properties, not randomly executed malformed programs or a
performance measurement.

```text
dotnet build tests/PreparationCode/Msync.PreparationCode.Tests.csproj -c Release -m:1 -nr:false -p:FrozenAssemblies=/absolute/frozen-image -p:ArtifactsPath=/absolute/msync/.temp/preparation-code
/absolute/msync/.temp/preparation-code/bin/Msync.PreparationCode.Tests/release/Msync.PreparationCode.Tests
```
