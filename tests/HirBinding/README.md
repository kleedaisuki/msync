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

`--invocation-reachability` checks the finite source-call graph used to decide
whether a concrete specialization needs an advance recursive result promise.
A class containing a closure does not establish recursion: only bound invocation
edges, including native Function-parameter callbacks, can return to that closure.
Unknown callees retain the promise path. The graph and its memo belong to one
bound program and contain no caller values or runtime object identities.

`--key-schemas` compares readonly and materialized specialization keys byte for
byte, including object aliases, distinct closure environments and the actual
List constructor capture graph. It checks that exact graphs allocate no formal
regions, while structural scopes, value captures and indexed resources retain
the normalized path. These are semantic/allocation-mechanism properties, not
latency thresholds.
