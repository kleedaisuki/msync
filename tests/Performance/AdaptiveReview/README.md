# Independent adaptive-runtime semantic review

This probe is maintained separately from the three prototype implementations.
It does not measure speed or modify product sources. Execute it against a frozen
prototype source tree by overriding the project-reference root:

```text
dotnet run --project tests/Performance/AdaptiveReview -m:1 \
  -p:MsyncSourceRoot=/absolute/path/to/frozen/source
```

The default 14 assertions execute explicit **executable-stage** bytecode through
the actual interpreter. That keeps the chosen call/field/Add sites present rather
than letting static optimization erase a guard discriminator. Selected programs
also pass through the binary codec. Checks cover:

- Same code identity with distinct closure cells, followed by a live mutation.
- Callee evaluation before an argument changes the callee's binding.
- Native identity under both direct-target and textual-table calls.
- A bad function override remaining an error, never launching a process.
- One field PC receiving three reordered layouts and a captured-field mutation.
- A warmed missing-field failure preserving its source span.
- Signed zero, NaN, String fallback and the existing raw mixed-Add failure.

`--source-order` is a separate source-compiler discriminator. It prints outcomes
at O0/O1/O2/O3 and retains IR dumps under `.temp/adaptive-independent-review`.
At discovery the original interpreter/source control returned AB at O0 but AA at
O1/O2/O3 for the expected-AB program. This is an existing optimizer fault, not a
new prototype result. The raw executable ordering case returns AB. Keep that
classification separate from performance comparisons and from the prototype's
correctness verdict.

These are bounded counterexample checks, not a universal equivalence proof.
The implementation owners separately verify retained O2 source sites and their
Native AOT timings; this review does not repeat their full matrix.
