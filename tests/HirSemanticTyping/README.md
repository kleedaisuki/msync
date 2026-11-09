# Typed HIR semantic typing journeys

This executable uses `Compiler.Analyze` directly, lowers its immutable result,
serializes the bytecode and executes both relocatable and context-bound images.
It does not use the old `Compiler.Compile` path as a substitute for HIR analysis.

The focused programs cover scalar operators, explicit casts, implicit Path/String
representation conversion, Null tests, Scope aliases and fixed fields, concept
requirements, actual typed native callbacks, indexed storage and stable snapshots.
Rejected programs distinguish type and null-admission failures before lowering.

```text
dotnet run --project tests/HirSemanticTyping -c Release
```

Use an isolated artifacts path while other implementation lanes build the shared
workspace. The fixture is an incremental semantic-boundary check, not a full
product regression or a replacement for the interactive/native acceptance lanes.
