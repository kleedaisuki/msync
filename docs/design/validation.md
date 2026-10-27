# End-to-end verification

The supplied `docs/dsl/spread.msync` and `implementation.md` define the expected language behavior. Independent CLI tests live in `tests/cli.rs`; they invoke the built binary, with each journey isolated beneath repository-local `.temp/`. Tests set environment variables on child processes only, so parallel tests do not race over process-wide environment or working directory.

## Verified journeys

Run `cargo test --test cli` for the focused acceptance suite. The current twenty-two tests cover:

- Stdin frontend-only `check` and metadata-inspecting `compile`, typed alias expansion, and absence of copy side effects.
- Real local overwrite followed by trailing-slash insertion conflict, including unchanged destination data and a located runtime diagnostic.
- Regex selectors matching whole names of direct children, excluding backup suffixes and descendants.
- Offline HTTP dry-run with no output or scratch directory creation.
- Formatter default in-place rewriting, explicit `--stdout` read-only preview, `--check` read-only failure on differences, comment retention, and repeated-write idempotence. REPL `/fmt` edits its script without executing it.
- A piped REPL session using `/help`, `/?`, `/cd`, `/pwd`, `/bindings`, `/check`, `/plan`, `/fmt`, `/load`, `/dry-run`, `/reset`, and `/quit`; bindings persist, `/check` bindings stay isolated, syntax/compiler failure does not end the session, and piped prompts stay suppressed.
- PATH lookup and ENV destination expansion using a controlled child-process environment.
- Unicode diagnostic source/gutter/caret, exact endpoint-qualifier highlighting and similar-binding suggestions, destination-operand location for a directory type mismatch, and the actionable trailing-slash fix.
- Actual ANSI highlights in both main and REPL help, plain piped output, forced ANSI color, `--color never`, and `NO_COLOR`. Help documents finite-copy, stream, buffer and aggregate-buffer-budget options.
- `$pwd::pwd/file` resolving relative to a REPL `/cd`; namespace aliases bound with `=>` remain dynamic, whereas values bound with `:=` capture their directory at declaration. Old `$pwd::arbitrary` and `$pwd/file` forms report migration help, while legacy bare local paths remain usable.
- `$file::this` and `$file::parent` distinguish same-basename scripts in different directories when invoked from an unrelated cwd. `/load` supplies a scoped script context; a namespace alias follows the next loaded file while a captured value stays fixed. Returning to direct REPL input or stdin does not invent a source-file context, nor an implicit `file` alias. Formatting preserves this namespace grammar without executing it.
- Rejection of an existing directory as a non-trailing-slash exact destination, before mutation.
- Live plan dead-store elimination of the first of two regular-file writes to one target. Planning leaves destination contents intact; bounded execution writes the final source and leaves both source files intact.
- CLI acceptance of all four resource limit options, and rejection of impossible/zero budgets before any target is written.
- Telemetry persistence after process exit; one shared trace ID, unique completed-span IDs, valid parent references, event-to-owner references, and one session root connecting parsing, compilation, execution, and copy. Compiler errors propagate to enclosing submission/session statuses. Useful path/error metadata is permitted; file payloads and automatic secret-environment snapshots are absent.
- An actual HTTP GET against a bounded localhost TCP server, including the expected base URL path and byte-exact downloaded body. This uses the native `curl` transport rather than a mock.

## Validation environment and limits

The initial acceptance run used Windows, Rust/Cargo 1.99.0, and the installed native curl client. `cargo test --test cli` passed all eleven journeys. Library, syntax/property, compiler, runtime, and telemetry suites also passed during the corresponding all-targets run; the installed-WSL-dependent runtime journey is explicitly ignored by default.

The focused CLI tests exercise piped handles and forced-color policy, not visual inspection in every supported terminal emulator. A real authenticated SSH server was not supplied. WSL round-trip verification requires an installed distribution and its explicit ignored test. Unix-only symlink behavior must also run on Unix; Windows results do not establish Unix-specific transport or filesystem behavior.

An early compiler test expected six copy operations in the seed; direct inspection shows five. The expectation was corrected rather than changing the parser to invent an operation. An early CLI telemetry implementation omitted the final flush of the process-global buffered writer; the retained exit-persistence test verifies the integrated correction.

## Final integrated verification

After compiler optimization and independent security review, the final Windows run on 2026-10-25 passed:

- `cargo test --all-targets`: 58 ordinary tests passed; the one environment-dependent WSL test remains ignored in the default suite. The benchmark target correctly skips timing in debug builds.
- `cargo test --test runtime -- --ignored --test-threads=1`: the real installed-WSL round trip passed separately, including a shell-metacharacter filename and no-clobber conflict.
- `cargo clippy --all-targets -- -D warnings` and `cargo fmt --check`: clean.
- `cargo build --release`: produced the release executable, which checked and compiled the supplied seed and performed an offline dry-run with hierarchical telemetry flushed to `.temp/seed-trace.jsonl`.
- A real ConPTY session exercised `/?`, persistent alias definition, `/dry-run`, a preview, a located unknown-endpoint diagnostic, and `/quit` without ending the REPL on the error. Pipe/forced-color/NO_COLOR policies remain covered by subprocess assertions.

Review regressions additionally cover Windows staging path escape, bidirectional source/destination tree overlap, Windows verbatim namespace aliases via file identity, SFTP glob escaping, literal HTTP glob characters, and formatter temporary-file ownership. See `review.md` and `runtime.md` for the exact fixes and their boundaries. `.github/workflows/ci.yml` defines Linux/Windows checks; that remote CI has not been run by this local session. Authenticated SSH remains the outstanding environment-specific integration limitation, not a claimed tested path.

## Authenticated SSH follow-up

The earlier live-SSH limitation is now closed for the actual host client/server
pair. `cargo test --test ssh -- --ignored --nocapture --test-threads=1` passed all
four isolated WSL SSH/SFTP journeys. They invoke the built Windows CLI with real
Windows OpenSSH clients and an independently configured chrooted WSL daemon;
existing client/server SSH and account files are fingerprint-checked unchanged.
See `runtime.md` for setup, exact coverage, discovered regressions and isolation
boundaries. Default all-targets runs deliberately ignore these four environment-
dependent tests rather than silently substituting a mock or skipping a requested
explicit run. No system SSH service or config was modified.

The final follow-up run passed 59 ordinary all-targets tests (five environment
tests intentionally ignored), all four explicitly requested SSH journeys, the
explicit WSL journey, and the README doctest. Strict all-targets Clippy, rustfmt,
release build and whitespace checks passed. The main SSH run took approximately
51 seconds on this host; these are correctness journeys, not throughput timings.

## Execution graph and bounded streams revision

`tests/scheduler.rs` verifies the revised graph executor through actual localhost TCP connections and files, not mocked completion callbacks. Run `cargo test --test scheduler` for these five focused journeys:

- Two independent TCP-to-TCP flows run alongside an ordinary file copy. The slow source deliberately withholds EOF; the fast destination must observe its own EOF and the file copy must finish before the slow source is released. This verifies separate finite/background lanes with `jobs = 1`, rather than merely checking a counter after completion.
- Twenty-four ordinary copies run under `jobs = 3`, followed by a dependency chain reading a newly produced target through another intermediate file. Final bytes and the reported maximum finite-copy concurrency are checked.
- Cancellation interrupts a source that stays connected without sending EOF. The executor must return an error within a bounded timeout, not successful completion or an indefinite wait.
- Four live TCP sources are requested with four stream slots, 1 KiB per-flow buffers, and a 2 KiB aggregate buffer budget. The first two are held open; the third connection must remain queued. After the first wave closes, the second wave completes. This verifies admission control against the memory budget independently of the configured stream-count limit.
- Zero jobs, stream slots or buffer capacity, and a memory budget smaller than one flow buffer, are rejected before execution starts.

The initial Windows run of this revised scheduler suite passed all five tests. Its timeouts bound failures; completion ordering is controlled by explicit channels and permits rather than relying on transfer speed. These checks establish the aggregate application-owned buffer contract, not a bound on total process RSS or operating-system socket buffers.

After CLI integration on 2026-10-26, `cargo test --test cli --test scheduler` passed all 22 current acceptance journeys (17 CLI, five scheduler). Source/diagnostic comparisons account for native Windows absolute-path escaping without changing path or endpoint semantics.

## Final 0.2.0 integration and installed acceptance

On 2026-10-26 the final Windows run of `cargo test --all-targets --locked`
passed **137 ordinary tests**, with nine environment-dependent tests excluded
from that default run. `cargo test --all-targets --locked -- --ignored
--test-threads=1` separately passed all nine: metadata-only WSL inspection,
the mounted Windows/WSL dependency regression, WSL copying, five isolated
authenticated SSH journeys, and a direct Windows-to-WSL-helper-to-TCP flow.
The SSH extension verifies 256 KiB FIFO and Unix-socket transfers in both
directions, observes an idle helper actually running before cancellation,
then confirms it exits. Existing SSH and account configurations remain unchanged.

The README doctest, strict all-targets Clippy, rustfmt, release build and
whitespace checks passed. The complete Linux all-targets suite and strict
all-targets Clippy also passed under WSL with a private tmpfs mounted on the
repository `.temp`; FIFO/socket creation is not supported on ordinary DrvFs.
The final Linux stream/pipeline rerun passed eleven native stream tests plus
three pipeline/preparation tests, including the newly added real IPv6 flow.
Build output stays in `target/wsl-streams`; dependencies reuse a package-manager
cache with offline resolution, without changing user configuration.

Key added acceptance boundaries are now independently exercised:

- 22 planner tests include 192 generated file-state programs, fixed-point
  idempotence, alternate ready-node execution order, overwrite elimination,
  source-version guards, stream effects and co-start graph diagnostics.
- Six persistent-stdin REPL tests prove help/status remain responsive during a
  live TCP flow, stop/quit join resources, wait/EOF semantics, owned error source,
  binding rollback on failed preparation, and linked async telemetry.
- A real two-FIFO chain co-starts all three flows, transfers 256 KiB unchanged,
  and reports three slots/3 KiB buffer reservation. An underbudget cohort fails
  before connecting. Unproduced missing sources fail live preparation before
  any writes; offline preview stays conservative.
- Destination EOF/FIN (explicitly including a one-way half-close), Windows
  write-only pipe disconnection, and Unix FIFO disconnection release an idle
  source without a probing write. Two slow/fast flows remain independent.

Finally, the release artifact was installed at `D:\SystemScripts\msync.exe`,
verified byte-identical to the build and reporting `msync 0.2.0`. Acceptance used
that installed executable, not Cargo's debug binary: `fmt` modified a script
in place, `--check` passed, `plan` showed two regular-file assignments reduced
to one, and resource-configured `run` copied the final source exactly once
without modifying either input. Installed help emitted real ANSI highlights.

## Explicit source namespaces and physical interruption ownership

On 2026-10-27, the focused command below passed **38 tests on Windows and 38 on actual WSL/Linux**: 22 CLI journeys, eleven interruption journeys, and five scheduler journeys.

```sh
cargo test --test cli --test interruptions --test scheduler
```

The namespace journeys distinguish lookup capability from a captured value: `space => $pwd` observes a later `/cd`, while `frozen := $pwd::pwd` retains its declaration-time directory. They execute same-basename scripts in separate directories from an unrelated cwd, copy `$file::this`, and resolve `$file::parent` against each actual script. A namespace alias declared in one `/load` resolves against the next loaded script; a captured path remains fixed. Direct REPL/stdin submissions do not inherit the previous load's file context. Legacy `$pwd::arbitrary` and `$pwd/file` uses fail with migration help, without changing ordinary bare-path behavior. Namespace formatting is checked independently of semantic execution.

`tests/interruptions.rs` uses the common `io::transfer` entrypoint for finite files and streams. Its tests observe real I/O state rather than merely asserting an end enum:

- Already-cancelled transfers preserve an existing target and create no transaction artifact. Ordinary file-to-file transfers use the same cancellation and source-EOF contract as socket flows.
- After both TCP endpoints open, token cancellation and forced task abort release both physical connections. The abort test explicitly leaves the cancellation token unset.
- After a socket sink acknowledges a binary prefix, abort closes the endpoints without injecting additional bytes. Source EOF flushes the exact binary payload and closes the destination normally.
- Windows directional named-pipe abort releases the writer and its duplicated close watcher, as observed by server EOF/disconnection. A backpressured regular-file-to-TCP flow is aborted after a received prefix; a Windows `share_mode(0)` open fails while the source handle is live and succeeds after cleanup, demonstrating physical handle release.
- Native Unix-socket and FIFO tests observe EOF after abort, including the FIFO writer's duplicated close-watch descriptor. These are real native filesystem endpoints, not mocks or metadata-only classifications.
- For regular-file sinks, a TCP source deliberately withholds EOF. The test waits until the same-directory unpublished stage contains a complete 4096-byte prefix, then cancels or aborts. This creates a stable after-write/before-publication boundary without sleeping or depending on copy speed. Existing targets retain their old bytes, new targets remain absent, and all stages disappear. A matching successful test publishes only after normal EOF.

The transaction assertion is **per regular file**. It does not promise to undo already committed files, structural directories, or bytes consumed by an external socket/pipe peer. The external-peer tests instead verify the observed exact prefix and physical closure. Completion counters are not used as a timing oracle for cancellation; actual received/staged bytes establish that I/O happened.

The Linux run used the private mount-namespace `.temp` tmpfs wrapper from `streams.md`, because DrvFs cannot create the required FIFO/socket fixtures. It used cached Rust 1.97.1, `CARGO_TARGET_DIR=target/wsl-performance`, `CARGO_NET_OFFLINE=true`, `CARGO_HOME=/mnt/c/Users/STONE/.cargo`, and `RUSTUP_HOME=/home/moesegfault/.rustup` as per-command environment settings. No package-manager or system configuration was changed. Windows used Rust 1.99.0. Both runs exercised native platform-specific tests rather than skipping them.

The isolated SSH fixture independently extends the same drop boundary: `ssh_fifo_socket_and_cancellation_use_real_remote_helpers` first observes a live remote Python helper, aborts the local transfer task while its token remains unset, and audits zero surviving helpers. The existing token-cancellation route remains separate. That actual fixture run passed; its remote setup and isolation remain documented in `runtime.md` rather than duplicated here.

The CLI trace-graph check now branches on the documented `kind`: completed spans own unique IDs, whereas lifecycle events reuse their owning span ID. Events must reference a span present in the same trace; they are not extra roots or graph vertices. This corrected a test assumption exposed by native Linux I/O events without weakening the single-session parent graph or payload/environment privacy assertions.

## Final v0.3.0 acceptance (2026-10-27)

The final source includes preparation-owned ancestor caching, ignored-directory-
suffix symlink guards and the known-COMMITTED/transport-timeout fix. Both platforms
ran the **default parallel** test harness, not only selected serial targets:

| Gate | Executed result |
| --- | --- |
| Windows `cargo test --locked --all-targets` | 207 passed, no failures |
| WSL/Linux `cargo test --locked --all-targets` | 202 passed, no failures; private native tmpfs fixtures |
| Both platforms `cargo clippy --locked --all-targets -- -D warnings` | Passed |
| `cargo fmt --check`, both release builds | Passed |
| Actual isolated Windows OpenSSH/WSL fixture, all six tests | Passed together, 81.36 seconds |
| Explicit native after-open cancellation and WSL round-trip runtime tests | Both passed |
| Explicit live WSL metadata batching/classification tests | Both passed |
| Actual Windows I/O Ring capability plus nonaligned positioned read | Passed, READ/WRITE supported, max version 400 |
| WSL regular file to real native TCP, and WSL/native alias-order regression | Both passed |
| Exact production Python file-helper protocol | Passed complete DATA/no-FINISH, truncated DATA, oversized frame and valid FINISH cases |
| Native POSIX source executable mode and unwritable-target checks | Both passed as UID/GID 1000 with capability bounding set cleared |

Ignored host-dependent or performance cases are not counted as passed in the
normal-suite totals. Windows-only SSH tests are not inferred from Linux's empty
target. The POSIX access test first proves a real nontruncating write-open is
denied before checking that atomic publication cannot bypass it.

The full rerun exposed two stale REPL fixtures whose old `$pwd` syntax caused
frontend errors instead of reaching their intended background/preparation
journeys. They now use `$pwd::pwd`, require an actually started background job,
and assert the actual existing-directory preparation diagnostic. A parallel
Windows HTTP fixture also exposed inherited nonblocking accepted sockets; test
servers now explicitly choose blocking accepted I/O with bounded read waits.
No production error was hidden by weakening those assertions or serializing
the final normal suites.

### Requirement-to-evidence audit

| Requested contract | Authoritative coverage |
| --- | --- |
| `$pwd` namespace, `this => $pwd`, captured `this := $pwd::pwd` | Compiler and actual CLI/REPL tests distinguish dynamic namespace lookup from declaration-time captured paths and preserve ordinary bare-path behavior |
| `$file::this` and `$file::parent` | Named-file/run/load/check/plan contexts, unrelated cwd, same basenames, transitive file aliases, missing stdin/REPL context and transaction rollback |
| Uniform `->` byte flow, structural directories | Public `io::transfer`, native finite file tests, actual TCP/FIFO/Unix socket/named pipe, HTTP and authenticated remote journeys |
| Open-resource cancellation, reset, abort and independent EOF | Actual accepted/staged prefixes, old/new target preservation, physical handle/peer EOF, helper process cleanup, real 256 MiB native-copy cancellation and empty remote FIFO cancellation |
| Remote commit knowledge | Explicit FINISH authorizes publication; real child-process tests distinguish known COMMITTED followed by shutdown/exit/timeout failure from an unacknowledged ambiguous timeout |
| Graph optimization and bounded execution | Fixed-point/state-equivalence properties, indexed overlap property, source-version/identity guards, hazard closure/co-start tests, finite/stream shared buffer admission and deferred child kinds |
| Cross-platform native I/O and event-driven performance | Actual CopyFileExW/TransmitFile and Linux range/sendfile tests, ring operations, 480 verified matched-storage samples and ten native-ext4 cold reactor flows |
| Millisecond startup and waiting | Actual paired CLI/REPL processes, native image/data and pathname-scope control, real WSL metadata clients, before/after tables in `performance-startup-v3.md` |
| Useful diagnostics/observability | Token/source-map and ANSI tests; paths, engine/phase, typed causes and committed/discarded counters; dropped active operations are incomplete/cancelled |

The storage challenge is resolved with opened-descriptor ext4 proof and verified
zero Linux-page residency for cold ext4 samples, not by relabeling tmpfs or
DrvFs as native disk. The durable 160-configuration CSV, ten-trial reactor CSV,
reproduction harness and scoped interpretation are in `io-linux-v3.md`. Host
physical cache is uncontrolled. Some deep DrvFs ring cases beat range copying;
the notes retain that counterexample and do not claim universal ring inferiority.

### Installed artifact journey

The verified default-profile release was deployed to `D:\SystemScripts\msync.exe`
and reports `msync 0.3.0`; command lookup resolves that same path. SHA-256:

```text
6D32BFABC48D9E70773113CA996ADDA0FA84AAA760E5BA7BB1C074B05566E419
```

It is byte-identical to `target/release/msync.exe`. The previous installed 0.2.0
image is preserved in `target/perf-baseline/msync-installed-before-v3.exe`.
Installation uses same-volume atomic replacement rather than in-place truncation,
not an unmanaged service or SSH-configuration change.

Acceptance then used the installed executable from an unrelated caller directory:
in-place `fmt` and `fmt --check`, contextual `check`, metadata-only `plan`, and
bounded `run --jobs 2 --buffer-size 1KiB --memory-limit 2KiB`. Four source statements
became three retained operations, with exactly three copied files and 206 flow
bytes. It copied the exact formatted `$file::this`, resolved `$file::parent` to
the script directory, resolved a namespace alias of `$pwd` to the caller directory,
preserved both input files, emitted useful trace path/ancestor counters and
forced ANSI help, and accepted both `/help` and `/?`. A separate existing exact
directory destination failed constructively without changing the directory.
Disposable commands/logs/fixtures remain under repository `.temp`; the durable
findings and procedures above do not depend on preserving that directory.

Final formatter safety also rejects both `fmt --dry-run` and `fmt -n` before
reading/writing a script, with guidance to use `--check` or `--stdout`. The real
CLI journey asserts the unformatted source stays byte-identical; a global
preview flag is never silently ignored while modifying the script.
