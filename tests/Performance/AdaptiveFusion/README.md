# P2: precompiled same-block addition fusion

This is an experimental overlay, **not an installed runtime feature**. Product
`src/` is untouched. `prepare.py` archives one exact revision into root `.temp`,
adds `Interpreter.Fusion.cs.template` to only the candidate, and records source,
probe and modified-file hashes. Keep control and candidate builds separate.

The source-generated O2 workload is a real parameterized Range accumulator. Its
callee cannot be inlined because it owns iteration; its parameterized loop is
not certified for compile-time evaluation. In the residual executable the
`LoadLocal; LoadLocal; Add` sequence remains in one basic block. A second source
uses an explicitly imported mutable outer location. These are not O0-only or
already constant-return performance cases.

The candidate builds site-local flags once per code image. The handler reads
both live values in source order, preserves all three cancellation checks, and
uses the original Add instruction/span for exceptions. Number addition uses the
same sequential double operation; String/deferred operands use the original
generic combination semantics with the values already read. It does not cross
block entries, calls, barriers, iterator callbacks or invocation owners. Session
image changes rebuild the plan. Serialized bytecode is not modified.

The earlier suggested `LoadLocal; GetMember` pair was deliberately rejected:
GetMember is a MayFail boundary and starts its own current basic block. A claim
of same-block fusion for that sequence would have measured zero eligible sites.

```text
python3 tests/Performance/AdaptiveFusion/prepare.py
# In each frozen control/candidate tree:
dotnet publish tests/Performance/AdaptiveFusion -c Release -r linux-x64 -o NATIVE_OUT
NATIVE_OUT/msync-fusion-probe --verify --sizes 1,32
```

Run timing only after all builds have stopped. Alternate the two native images,
record their hashes, and retain both preparation and prepared/first-execution
costs; do not report a warm-loop gain as a startup or file-copy improvement.
`last`, `literal`, and 4 KiB `copy` are negative controls with no eligible site.
All scenarios consume their result or check actual file bytes. The probe checks
source, relocatable, executable and binary round-trip execution separately.

Selected arithmetic errors, String fallback, IEEE negative zero/NaN and
cancellation have bounded mechanism discriminators. Independent review is
separate from this experiment's own byte/value oracle.
