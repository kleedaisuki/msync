# Higher-order specialization and recursive fixed points

This focused executable uses the production compiler, binary codec, contextual
binding and interpreter. It distinguishes ordinary nested calls of one generic
function from genuine recursion. Sharing runtime kinds is not enough: callbacks,
Scope member schemas and captured callable identities affect specialization.

The nested Result fold reproduces the source-library `Result::zip` failure
without depending on the public spelling of that library. Two active calls to
the same generic `fold` have different callbacks and payloads; neither may reuse
the other's unfinished `Never` result. Three nested calls also exercise active
frame restoration. Recursive programs retain the reserved specialization even
when each call constructs fresh Scope metadata, so later mutable-call rendering
does not infer an unbounded sequence of equivalent recursive bodies.

Private nullable locals and union-admitting parameters preserve their selected
return value types, while external live locations retain their future domains.
Deferred free reads and immediate callee-body flow are distinct phases. Tests
cover type-changing live unions, captured mutation, readonly-parameter lexical
shadowing and mutable Scope payloads; none rely on eager argument freezing.

Each program executes at O0/O2/O3 from source, serialized `.mso`, and serialized
context-bound `.mexe`. The same source is analyzed through the actual editor
compiler to check that the inferred result remains useful for completion.

```sh
dotnet run --project tests/HigherOrderCalls/Msync.HigherOrderCalls.csproj \
  --artifacts-path .temp/higher-order-calls
```

Use an isolated artifacts path when other teams are building the shared product.
No alternate evaluator, recursion limit or source-library workaround is used.

The focused managed run passed 192 contracts: 162 staged executions, eighteen
editor result-type checks and twelve unsafe-refinement rejections. See the mechanism in
[`generic-specialization-v080.md`](../../docs/design/generic-specialization-v080.md).
