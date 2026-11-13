# Concurrent real-process test execution

2026-11-13 implementation and rationale. The framework is development-only typed
Python in `scripts/test.py` and `scripts/testing/`; product execution remains
native .NET. [Commands, result schema and adapters](../../tests/TestExecution/README.md).

## Ownership model

An immutable `Task` names an argv command, CPU/memory reservation, locks,
exclusive scheduling, timeout, optional receipt and compiler use. Existing
journey scripts retain their independent assertions and public command lines.
The default selected set is semantic, detached artifact and LSP journeys;
selection is explicit rather than a heuristic proof about changed source paths.

One repository lease owns shared incremental build outputs and admits one suite
at a time. A ready-task queue admits independent journeys within the budget,
with locks for shared fixtures and an exclusive reservation for benchmarks.
A waiting exclusive reservation prevents later work from overtaking it. No
custom assertion DSL, generic build DAG, distributed scheduler or result cache
is introduced.

MSBuild owns incremental rebuilding in a stable build tree. **There is no custom
source/environment fingerprint cache.** The controller copies one resulting
product payload per run, recording hashes only as execution identity and to
avoid accepting a mixed payload while copying. C# contract projects with an
existing `FrozenAssemblies` contract reuse those fixed product DLLs and isolate
their own outputs.

## Supervision and resource accounting

Run scopes and SDK-specific Roslyn services are peers under the user
`msync-tests.slice`. Its CPUQuota/MemoryMax/MemorySwapMax enforce the combined
12 CPU / 24 GiB ceiling, including persistent compiler memory and CPU. The active
lease holder applies any smaller requested budget. Queued controllers do not
obtain an independent workload allowance.

A dedicated `SharedCompilationId` connects clients to the framework-owned
VBCSCompiler service. Normal completion leaves it warm across runs; its standard
idle lifetime releases unused resources. Other projects' default servers remain
untouched. Per-client protocol logs prove whether actual compilation reached
the server, rather than treating a running PID as evidence of compiler use.
The controller records service PID, reuse, cgroup and before/after CPU/memory
snapshots. Compiler task cancellation/timeout stops the owned service to release
outstanding compile work; this is an exceptional termination path, not the
normal compilation policy.

Each task has a child cgroup and process group. The supervisor forwards signals,
waits briefly for ordinary termination, then kills and empties the owned cgroup.
This includes descendants that call setsid. Normal process exit also reaps
leftover task descendants, while the shared compiler has separate service
ownership. Root-process completion alone is not sufficient cleanup evidence.

Task resource declarations guide admission; kernel limits enforce the shared
ceiling. Per-task CPU/memory describe client-owned descendants. Shared compiler
CPU must not be charged inclusively to every simultaneous client, and shared
slice memory peaks are lifetime values rather than per-request allocations.

## Persistence and failure semantics

One controller writes SQLite with WAL. The store persists immutable specs,
actual commands/cwd, queue and execution boundaries, child PID/exit status,
resource decisions, concrete infrastructure exceptions and causal phase events.
Full output, receipts and compiler-client logs stream as 64 KiB byte chunks,
not truncated text; JSON tails are only convenient summaries.

Failures, timeouts, cancellation and infrastructure errors remain distinct.
The current independent work drains unless fail-fast is selected. Fail-fast is
a failed run, not a user interruption. A zero-exit task missing a declared receipt
is not complete. Manual replay uses new working directories and the original
fixed image unless explicitly replaced; it never rewrites earlier outcomes.

Functional journeys can share resources. Exclusive non-compiler work freezes
the owned compiler cgroups and resumes them afterward, preserving warm state
without background compiler GC during measurement. A benchmark reserves the suite
exclusively and retains the benchmark runner's own timing boundaries; its
throughput child reuses the admitted cgroup instead of escaping to another
scope. Outer task times are not benchmark scores.

## Real-process acceptance and discoveries

Retained receipts: `artifacts/validation/test-execution-v090/`.

- One published Native AOT image passed semantic, detached compact artifact and
  LSP journeys with concurrent task intervals. Original script oracles remain.
- A real mixed process journey verified overlap, resource-lock serialization,
  exclusive ordering, native source diagnostics, launch errors, timeouts,
  fail-fast, replay, raw NUL/non-UTF-8 output, signal forwarding and detached
  descendant cleanup. Two controllers verified global admission ordering.
- A declared receipt was copied byte-for-byte; zero exit without the required
  receipt remained an infrastructure error.
- A bounded throughput child completed inside an exclusive framework task,
  retaining its own raw benchmark database and existing oracle checks.
- The default build path produced an isolated managed apphost. The CLI journey
  exposed an obsolete bytecode-only stdout marker assertion. It now accepts the
  native IR and historical bytecode binary framing, without changing product
  artifact admission or masking textual banners. Replay on the same image
  passed the actual CLI/formatting/non-replaying REPL journey.
- The C# adapter exposed `HirEditorSyntax` omitting the new EditorLexemes source
  link. Adding that actual dependency fixed compilation; replay passed its
  original syntax contracts with fixed product DLL identities.
- Two separate C# compilations used the same live SDK compiler PID; both client
  logs explicitly record server-processed compilation. The service and clients
  are in the same bounded slice. No speedup claim is inferred from the small
  acceptance windows.

## Engineering and research context

[Bazel's execution contract](https://bazel.build/reference/test-encyclopedia)
illustrates the value of declared dependencies and predictable environments;
we borrow ownership principles without migrating the build system or turning
real SSH/editor work into mocked execution.

[Practical Regression Test Selection](https://mir.cs.illinois.edu/marinov/publications/GligoricETAL15PracticalRTS.pdf)
(ISSTA 2015) motivates optimizing the complete feedback path, including selection
and dependency overhead, rather than selected-test counts. No automatic semantic
selection solver is needed for this explicit focused runner.

[Resource-related flaky tests](https://arxiv.org/abs/2310.12132) motivate budgeted
admission rather than unconstrained concurrency; their study is not an estimate
of msync's failure rate.

Roslyn's [compiler server design](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md)
provides the actual JIT/metadata reuse mechanism. Installed SDK targets expose
`SharedCompilationId`. [NamedPipeUtil](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Shared/NamedPipeUtil.cs)
uses `/tmp/<pipe>` on Unix independently of TMPDIR; the server-readiness probe
follows that implementation, rather than assuming CoreFxPipe naming. This is
runtime IPC, not a project artifact outside the repository.

[systemd resource control](https://www.freedesktop.org/software/systemd/man/latest/systemd.resource-control.html)
provides hierarchical enforcement for the persistent compiler and transient
workers without a new framework daemon or privilege changes.
