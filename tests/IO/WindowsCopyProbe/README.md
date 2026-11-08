# Windows copy mechanism probe

This standalone Native AOT experiment compares the current ordinary-file I/O
mechanism (overlapped `FileStream.CopyToAsync`, a 64 KiB payload buffer, metadata,
one explicit flush and sibling-stage atomic replacement) with native
`CopyFileExW` under the same publication contract. It is not a product backend.

```
dotnet publish tests/IO/WindowsCopyProbe/WindowsCopyProbe.csproj \
  -c Release -r win-x64 --artifacts-path artifacts/io-probe \
  -o artifacts/windows-copy-probe
artifacts/windows-copy-probe/WindowsCopyProbe.exe \
  --output .temp/platform-results/windows-copy-probe.json
```

The native candidate uses one worker per selected fixture/tree, never one worker
per chunk. CopyFileEx creates a unique sibling stage with FAIL_IF_EXISTS and no
restartable/unbuffered flag. Its first-stream callback duplicates the already-open
destination handle. After native completion the candidate flushes that retained
handle once, closes it, checks cancellation, and replaces the target. This avoids
reopening a source-readonly stage for write and does not add a second flush per
alternate stream. Neither candidate claims universal power-loss durability.

There are two warmups and nine alternating paired samples per ordinary-file
size (4 KiB, 1 MiB, 64 MiB) and for a pre-enumerated 256×4 KiB file cohort.
Fixtures contain nontrivial deterministic bytes. Stage creation, copy, metadata,
flush and replacement are timed; source generation, hashes and output checks are
not. Raw wall-clock and process CPU samples plus medians are JSON fields.
Directory enumeration/planning and CLI startup are intentionally outside this
component probe. Actual product source/object/package timings are separate.

Cancellation is triggered only after actual positive transfer: the native API's
progress callback, or a write-completion observer around the current managed
copy's output stream. Only the cancellation trial has that observer; timed normal
copies use the unwrapped FileStream. Both trials require the prior published
bytes to remain and live staging to be removed. The JSON records cancellation to
completion latency and positive copied bytes, not merely a pre-cancelled token.

Untimed checks cover source mtime, NTFS named streams, readonly attributes,
long paths and empty files. The current managed backend's named-stream result is
recorded, not silently treated as equivalent to native metadata preservation.
No performance conclusion is available until this executable runs on actual
Windows; a successful Linux SDK build establishes only compilation/AOT analysis.
The platform workflow runs this probe on the same Windows host as the packaged
application and legacy Rust user journeys.
