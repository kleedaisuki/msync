# Native I/O and Process acceptance

The managed executable exercises native builtin byte/text operations, reusable
file readers, atomic replacement after an invalid byte, actual child pipes,
PID identity, stop/wait and cancellation after child admission. `--standard-streams`
checks O0/O2 source, strict portable round-trip and prepared round-trip with
borrowed readers/writers, independent line endings, prompt flush and cancelled
input followed by a successful read from the same reader.

```console
dotnet run --project tests/StdlibIOProcess -c Release
dotnet run --project tests/StdlibIOProcess -c Release -- --standard-streams
python3 tests/StdlibIOProcess/native_journey.py --binary PATH_TO_NATIVE_CLI --child PATH_TO_TEST_EXECUTABLE
```

The CLI journey defaults to O0/O2 and source/HIR/object/bound routes. Each route
has a fresh directory and exact file-byte oracles. Source is removed before HIR
or object execution; source and portable inputs are absent before bound execution.
`--case`, `--level` and `--route` select a focused question. Unique work directories
under `.temp/stdlib-io-native/` retain image/dependency manifests, artifact/source
hashes, per-command stdout/stderr and a checkpointed `result.json`, including
failures rather than deleting their evidence.

File-reader controls cover a path created after range construction, `take(0)` on
never-created paths, repeated enumeration across writes, captured directory
origins and explicit acquisition. Real CLI stdin, prompt output and stderr have
independent byte oracles, in addition to the injected-stream ownership checks.
An unrelated pending Process-output transfer
must not prevent reading another file: the child is released only after that
read, so joining every pending transfer would deadlock rather than pass.

On a host with the maintained loopback OpenSSH fixture:

```console
python3 tests/StdlibIOProcess/remote_journey.py --binary PATH_TO_TEST_EXECUTABLE
python3 tests/StdlibIOProcess/remote_native_journey.py --binary PATH_TO_NATIVE_CLI --child PATH_TO_TEST_EXECUTABLE
```

These retain configured aliases and check actual SSH client, authentication and
channel counts, with no SCP subprocess and no authentication during compile/bind.
The native Process reports the real remote child PID; source namespace member
reads compare its stdout against that PID, not a proxy PID or merely nonempty
output. Raw native storage is not used to bypass VM-owned pending fields. The
remote CLI routes also delete source/object inputs before detached execution.
These are functional ownership/transport checks, not throughput measurements or
claims about an unexecuted physical platform.

The 521-input Linux x64 integration image (`79102922519d9261…`) passed all 104
local routes and 12 remote routes on one unchanged payload. The independently
audited index is `artifacts/validation/native-io-integration/acceptance.json`, with
the source manifest alongside it. This is an integration checkpoint, not a
release-completion claim. Earlier failing ordering, directory-origin, HIR-arity
and mixed-lock snapshots remain separate rather than being overwritten.
