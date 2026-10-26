# End-to-end verification

The supplied `docs/dsl/spread.msync` and `implementation.md` define the expected language behavior. Independent CLI tests live in `tests/cli.rs`; they invoke the built binary, with each journey isolated beneath repository-local `.temp/`. Tests set environment variables on child processes only, so parallel tests do not race over process-wide environment or working directory.

## Verified journeys

Run `cargo test --test cli` for the focused acceptance suite. The revised seventeen tests cover:

- Stdin frontend-only `check` and metadata-inspecting `compile`, typed alias expansion, and absence of copy side effects.
- Real local overwrite followed by trailing-slash insertion conflict, including unchanged destination data and a located runtime diagnostic.
- Regex selectors matching whole names of direct children, excluding backup suffixes and descendants.
- Offline HTTP dry-run with no output or scratch directory creation.
- Formatter default in-place rewriting, explicit `--stdout` read-only preview, `--check` read-only failure on differences, comment retention, and repeated-write idempotence. REPL `/fmt` edits its script without executing it.
- A piped REPL session using `/help`, `/?`, `/cd`, `/pwd`, `/bindings`, `/check`, `/plan`, `/fmt`, `/load`, `/dry-run`, `/reset`, and `/quit`; bindings persist, `/check` bindings stay isolated, syntax/compiler failure does not end the session, and piped prompts stay suppressed.
- PATH lookup and ENV destination expansion using a controlled child-process environment.
- Unicode diagnostic source/gutter/caret, exact endpoint-qualifier highlighting and similar-binding suggestions, destination-operand location for a directory type mismatch, and the actionable trailing-slash fix.
- Actual ANSI highlights in both main and REPL help, plain piped output, forced ANSI color, `--color never`, and `NO_COLOR`. Help documents finite-copy, stream, buffer and aggregate-buffer-budget options.
- `$pwd::file` and `$pwd/file` resolving relative to a REPL `/cd`, while legacy bare paths remain usable.
- Rejection of an existing directory as a non-trailing-slash exact destination, before mutation.
- Live plan dead-store elimination of the first of two regular-file writes to one target. Planning leaves destination contents intact; bounded execution writes the final source and leaves both source files intact.
- CLI acceptance of all four resource limit options, and rejection of impossible/zero budgets before any target is written.
- Telemetry persistence after process exit; one shared trace ID, unique span IDs, valid parent references, and one session root connecting parsing, compilation, execution, and copy. Compiler errors propagate to enclosing submission/session statuses. Paths, file contents, and source text are absent.
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
