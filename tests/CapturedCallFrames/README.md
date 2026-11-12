# Split captured call activations

Run the focused managed runtime contracts and the same programs through a
published CLI:

```sh
dotnet run --project tests/CapturedCallFrames -c Release \
  --artifacts-path .temp/captured-frame-test-build
python3 tests/CapturedCallFrames/journeys.py .temp/current/msync \
  --work .temp/captured-frame-cli
```

The raw format-6 fixtures are emitted by the actual Core artifact writer
with typed layouts, function signatures and capture kinds.
The CLI binds the standalone .mso before collecting any profile and runs
both portable and bound routes. The ordinary source fixture deliberately
uses O0 to retain a call with two live lexical captures. The .NET SDK also
builds the fixture's echo child; it is not needed by the product JIT.

Frame checks distinguish `borrowed_frames`, their `split_frames` subset,
`detached_frames`, and `raw_arguments` counted only in actual borrowed calls. Distinct captured layouts and a live operand below
the call detect argument/capture offset mistakes. The same closure is
reused after a write to its existing aliased Cell, a slot replacement,
and a parameter's promotion to a fresh Cell. An ordinary argument read
through a caller Cell must detach when rebound; the original Cell does not
change. Raw location arguments are rejected by the format-6 reader.
Calls requiring extra local storage and persistent session frames
remain unborrowed even when the caller produces an unboxed argument.

The raw Number fixture passes arithmetic results to both the contiguous and
split layouts. It checks negative zero and a quiet NaN's payload bit for bit
on direct return, retains an old operand across a parameter replacement,
and checks raw-to-Cell promotion and the other argument after detachment.
A gated input reader forces the borrowed callee to suspend; the resumed
result depends on raw operands both within and below the borrowed slice.
The CLI checks the same observable results on portable and bound routes;
the in-process test additionally inspects the returned Number bits.

Three real subprocess cases launch an echo child in a multi-capture call
and let it escape by return value, an original Scope argument after the
callee rebinds its parameter, or a captured Cell. The child waits for
stdin, so prematurely joining it prevents the caller from sending the
echo payload. In the rebound-Scope case a raw Number precedes that Scope
in the original argument slice and is read after detachment. Exact echo
output and split/detach/raw counters must all match on the portable and
executable routes.

The effect-tail fixture alternates read-only, captured-Cell write, and
ordinary-parameter commit over the same live Cells. All three receive 137
raw arguments. On supported x64 hosts, the read-only and ordinary commit
tails each execute natively 74 times, while the captured write stays
canonical. Only the ordinary commit detaches, once per call, and the
reader must see the writer's current value. The CLI also checks
`MSYNC_JIT=off` and instruction-site mode as non-native controls. Use
`--semantic-only` to run the same oracles without the frame-counter assertions.
SQLite profiles and the JSON receipt stay in the chosen repository-root `.temp`
workspace. No timing thresholds or network dependencies are used.
