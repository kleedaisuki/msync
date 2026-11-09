# Integrated language contracts

This focused executable checks interactions rather than repeat the Number,
Concepts or NamedScopes component matrices. It exercises static explicit casts,
lazy Range element conversion, Path/String-only implicit conversion, left-to-right
single evaluation of physical qualification, composed concept-returning singleton
closures, nested `global` captures, nullable refinement and fixed storage types.

Every successful program runs as source bytecode, a binary `.mso` round trip and
a bound binary `.mexe` round trip at O0, O2 and O3. Invalid source checks require a
useful compiler diagnostic rather than a runtime failure.

After the shared managed producer has built current Compiler and Runtime output:

```text
dotnet restore tests/LanguageUpgrade/Msync.LanguageUpgrade.Tests.csproj
dotnet build tests/LanguageUpgrade/Msync.LanguageUpgrade.Tests.csproj --no-restore -p:BuildProjectReferences=false
dotnet run --project tests/LanguageUpgrade/Msync.LanguageUpgrade.Tests.csproj --no-build
```

## Accepted observation

On November 9, 2026 the shared managed snapshot 1 (SDK 10.0.403) passed all
232 assertions: 24 successful source programs through three stages and three
optimization levels, plus 16 static rejections. The test-only build above
reported zero warnings and errors. No product project was rebuilt by this
validator, and the result does not claim Native AOT or Windows acceptance.
