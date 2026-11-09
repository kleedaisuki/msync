# Closed-Type metaprogramming

This focused executable checks every pure `$std::meta` operation, finite-union
and nullable properties, implicit versus explicit conversion, Range element
schemas and the actual source / relocatable / executable journey at O0/O2/O3.
The independent relation examples include incompatible Range element types,
which must not be admitted merely because both outer kinds are Range.

```sh
dotnet run --project tests/MetaTypeValues/Msync.MetaTypeValues.Tests.csproj \
  --artifacts-path .temp/meta-type-values/build
```
