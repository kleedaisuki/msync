# Captured locations in returned Ranges

Returned Ranges can contain closures whose captured cells are written after
the Range expression is constructed. Their semantic element information must
be instantiated from the completed callee state, just like a closure returned
directly or through a Scope. Keeping the Range's constructor-time Function
descriptor incorrectly proved an initially Null cell was still Null after a
later Number, Scope or Process assignment.

The result graph instantiator traverses Range elements and callable alternatives.
It freshens callee-owned locations once per call site and specialization while
retaining borrowed argument/capture identities, including cells reachable through
callback values. Scalar and scalar-Range results skip the ownership graph work.

Eager inference and deferred lowering may compile different specialized bodies,
with different nested closure identities. Their result-instance cache therefore
includes the selected specialization identity; reusing the eager probe's closure
metadata for the deferred body produces dispatch tables missing the runtime
closure's identity.

The focused executable passed 57 contracts at O0/O2/O3: six programs through
source, serialized `.mso` and serialized `.mexe`, plus three rejection checks for
an unsafe live Range/String capture. Cases exercise late scalar/record writes,
multiple closure alternatives, independent factory cells, borrowed Range records
and borrowed callback-result records. Live Process ownership is exercised by the
separate `ReturnedProcesses --late-cell-only` journey.

```sh
dotnet run --project tests/LateCapturedRanges \
  --artifacts-path .temp/late-captured-ranges
```
