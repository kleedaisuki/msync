# End-to-end verification

The supplied `docs/dsl/spread.msync` and `implementation.md` define the expected language behavior. Independent CLI tests live in `tests/cli.rs`; they invoke the built binary, with each journey isolated beneath repository-local `.temp/`. Tests set environment variables on child processes only, so parallel tests do not race over process-wide environment or working directory.

## Verified journeys

Run `cargo test --test cli` for the focused acceptance suite. Eleven tests cover:

- Stdin `check` and `compile`, typed alias expansion, and absence of filesystem mutation.
- Real local overwrite followed by trailing-slash insertion conflict, including unchanged destination data and a located runtime diagnostic.
- Regex selectors matching whole names of direct children, excluding backup suffixes and descendants.
- Offline HTTP dry-run with no output or scratch directory creation.
- Formatter print/check/write, comment retention, and repeated-write idempotence.
- A piped REPL session using `/help`, `/?`, `/cd`, `/pwd`, `/bindings`, `/check`, `/plan`, `/fmt`, `/load`, `/dry-run`, `/reset`, and `/quit`; bindings persist, `/check` bindings stay isolated, syntax/compiler failure does not end the session, and piped prompts stay suppressed.
- PATH lookup and ENV destination expansion using a controlled child-process environment.
- Unicode diagnostic source/gutter/caret, plain piped output, forced ANSI color, `--color never`, and `NO_COLOR`.
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
