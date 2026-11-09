# Name-bound generic HIR

This focused executable checks the syntax-to-semantic boundary independently of
bytecode layout. Bound bodies use the same structured HIR arena as specialized
bodies, but retain explicit type parameters until semantic instantiation. No
syntax node or bytecode instruction is retained by the binding result.

The cases distinguish declaration identity, lexical shadowing, initialization
timing, recursive self, transitive location capture, global imports, named Scope
identity, Range item timing and exact source ownership. Standard module references
reserve one shared logical cell without eagerly parsing every library body.
Editor-only records identify declaration visibility and member/receiver spans;
they are not reconstructed from generated instructions.

```text
dotnet run --project tests/HirBinding/Msync.HirBinding.Tests.csproj
```
