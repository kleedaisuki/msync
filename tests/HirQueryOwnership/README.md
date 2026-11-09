# Semantic query ownership

This real-source test binds and analyzes query expressions, compares executable
payload/location/function tables with the bound input, serializes the semantic
module, reads it without source binding, and executes the pure lowerer's output.
It covers structured Match/For/Scope queries, recursive signatures and isolated
location/object writes. Direct context checks verify that query and flow-fork
contexts cannot acquire an output capability and that query construction does
not allocate in proportion to a borrowed lexical world.

Submission transaction checks distinguish real generation acceptance from
inference queries: existing row writes, appended functions/locations/payloads,
export references and newly interned types must rewind after an abandoned
source generation. Nested commit/outer rollback and checkpoint allocation with
100000 accepted exports are covered without scanning or copying that prefix.

Run with `dotnet run --project tests/HirQueryOwnership -c Release`.

`--allocations path.msync` runs only the allocation probe: it separates binding
from semantic analysis, records arena/table sizes, and checks an equivalent
public `Compiler.Analyze` call. Freeze/sealing replay allocations are reported
separately as a representation-cost estimate, not added to the measured semantic
total a second time. One warm-up precedes three samples; these observations are
not allocation or elapsed-time test gates.
