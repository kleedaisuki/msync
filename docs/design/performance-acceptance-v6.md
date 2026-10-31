# Cross-domain performance and tooling acceptance

## Scope

This is the independent acceptance plan for the post-language-v0.6 optimization,
configuration, local observability, editor/LSP, and interactive-latency workstream.
The earlier language release gates are a useful compatibility reference, **not**
evidence that this new workstream is complete. The root owns the roadmap and
measurement baseline; this document owns cross-domain user journeys rather than
production implementation, isolated provider tests, or a second benchmark suite.

Current starting code has opt-in JSONL telemetry, typed compilation and execution,
and an interactive editor. Config TOML, SQLite persistence, and actual LSP stdio
are new contracts under active implementation. Tests must follow the accepted
public interfaces as they land, while keeping independently derived behavior
(no source execution by an LSP, causal parent identity, finite-state cancellation,
correct file bytes) separate from incidental implementation details.

## Acceptance matrix

| User journey | Observable contract | Independent coverage / owner boundary |
| --- | --- | --- |
| Ask for help/version with malformed config present | No config failure, telemetry database, log file, or generated user directory | CLI subprocess with isolated HOME/config roots; domain owner separately tests TOML parsing |
| Disable configuration | No default/configured persistence side effects; explicit CLI options retain their normal meaning | Real `--no-config` invocation; directory snapshot before/after |
| Configure then override one value at the CLI | Defaults < config < supported environment < explicit CLI; unrelated configured values remain | Actual file operation and inspectable effective setting/trace, not just deserialization |
| Diagnose invalid key/value | Nonzero exit before file effects; names offending key/value and source config, with a useful repair | Misspelling and invalid limit cases; no exact prose lock-in |
| Successful traced synchronization | Correct bytes plus one queryable causal graph through session/request/compile/preparation/block/scheduler/instruction/I/O | Config-selected SQLite sink, independent database/query/export consumer |
| Failed synchronization | Original diagnostic paths and OS cause remain visible; failed causal records survive exit | Missing input/readonly destination; query persisted outcomes after process exit |
| Cancel a blocked flow | No partial publication, process remains responsive, terminal cancellation persisted | Local TCP source handshake plus REPL stop; no sleep-only race trigger |
| Open/edit a Unicode document in an editor | Versioned actionable diagnostics; UTF-16 locations and semantic token lengths; valid edit clears errors | Actual stdio JSON-RPC process, independent client; lexical provider unit tests remain frontend-owned |
| Format an edited document | Canonical edits are applicable and idempotent; no synchronization effects | Apply LSP edits using independently checked UTF-16 boundaries |
| Navigate and complete code | Declared capabilities agree with useful responses; lexical definitions do not leak across independent scopes | Actual requests once source-index contract lands; provider precision tests remain frontend-owned |
| Background work ends while input is idle | Completion and committed output become visible without a new input line; subsequent commands remain responsive | Held local TCP source, then release while stdin remains open and untouched |
| Optimize copy/selector/control work | Exact output bytes, deterministic selection, runtime existence freshness, and source-free object execution remain unchanged | Combined real CLI check/preview/run/object journey; owner tests separately check optimizer mechanisms |
| Improve user-visible latency | Startup and phase gaps are measured with preserved baseline and representative small controls | Root/latency owners run serial release measurements; acceptance tests avoid brittle wall-time speed assertions |

## Test artifacts and coordination

- `tests/performance_lsp_journeys.rs` uses an actual language-server subprocess.
  Its framing client in `tests/support/lsp_client.rs` rejects accidental stdout
  chatter, supports asynchronous diagnostics, drains stderr, and has bounded
  waits and child cleanup.
- `tests/performance_journeys.rs` combines real optimized copies, deterministic
  selectors, mutable control and source-free objects; a released child program
  completing while REPL input remains idle; configuration-relative SQLite paths
  with explicit CLI overrides and read-only operator queries; cancellation of a
  blocked TCP flow with persisted causal records; and invalid configuration that
  must not initialize its selected database or runtime state.
- Configuration syntax/discovery/provenance is defined in
  [configuration-v6.md](configuration-v6.md); its domain CLI tests already cover
  help/version bypass, fallback selection, invalid source locations and actual
  spool limits. SQLite domain tests cover sink API behavior, failure persistence,
  read/query/export, and explicit-disable startup. The cross-domain target adds
  their interaction rather than duplicating each table-driven domain test.
- The accepted runtime vocabulary is `interpreter.block` (block identity and
  instruction count), `interpreter.prepare`, `interpreter.schedule` (node/edge
  counts), and `interpreter.instruction` (instruction identity/kind).
  `scheduler.ready` events expose readiness/active-work counts; `runtime.wait`
  identifies a genuine wait cause. The configured-success journey checks the
  applicable block children, IDs, readiness metric and absence of orphaned
  spans, rather than requiring impossible I/O phases on a pure `--version`.
  Successful REPL `/stop` must persist cancellation on its underlying operation
  while leaving the normally completed REPL/session successful.
- `tests/support/journey_process.rs` isolates configuration environment variables
  and concurrently drains both process streams. This prevents verbose trace or
  error output from deadlocking a test that waits for child exit first.
- Disposable fixtures, databases, exports, and local command logs belong under
  repository `.temp`; this plan and test sources preserve useful knowledge if
  those files are cleaned.

Cargo execution is root-owned during integration. No background Cargo invocation
or benchmark is started by this validator while baseline binaries are frozen or
other domain owners change shared production code. Run only the newly affected
journey targets and expand after a failure reveals a concrete shared seam.

## Progress and evidence

The root-owned native Linux gate captured in
`.temp/performance-v6/features-remaining.log` exercised the selected targets
(reproduction command):

```sh
cargo test --locked --test performance_journeys --test performance_lsp_journeys
```

- All **five cross-domain configuration/SQLite/runtime journeys passed** in
  0.81 seconds, including idle released-process completion, cancellation
  persistence, policy-relative databases and preserved optimized file bytes.
- The actual stdio include-diagnostic provenance/repair journey passed.
- The editor roundtrip passed its UTF-16 diagnostics, semantic token and
  incremental repair stages, then exposed a **test expectation error**: the
  established formatter parenthesizes binary expressions, so canonical output
  is `flag = ("🎇" == "🎆")`, not the test's unparenthesized version. Only that
  expected literal was corrected. Production formatting, semantic assertions
  and the independent second-format idempotence check were unchanged.
- The corrected roundtrip **passed** the root's focused rerun captured in
  `.temp/performance-v6/final-functional.log`: one selected test passed in
  0.45 seconds, with the already passing foreign-source test filtered out.
  This completed its later actual completion, hover, definition and document
  symbol requests, plus formatter idempotence and orderly shutdown.

All **five runtime/configuration/SQLite journeys and two real-stdio LSP
journeys** have passing evidence across these incremental gates. This is not a
claim that both complete targets were rerun together after the test expectation
correction. The full passed target was not repeated solely for a test-only change
in the other target. No authored journey in these targets remains unexecuted.

These are new-workstream functional acceptance observations, not performance
measurements or evidence of a complete Windows/native-platform matrix. Root and
domain owners record their separately selected tests and serial release
measurements. Previously passed targets are not repeated without a relevant
change or failure pointing into their shared implementation seam.
