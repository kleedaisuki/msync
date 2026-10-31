# Configuration, provenance, and pre-effect diagnostics

## Boundary

Configuration changes application policy, not DSL meaning or object contracts.
The CLI resolves one optional TOML configuration file before compilation or
execution, overlays environment compatibility and explicit command-line values,
and validates the resulting settings before any transfer, telemetry database or
runtime scratch admission. `msync config` displays the effective settings as
parseable TOML with a `# source: ...` comment immediately before each value.
Inspection never creates the configured state or telemetry destinations.

`--help` and `--version` do not open configuration files or initialize tracing,
including when `--config` names a corrupt or nonexistent file. This preserves
the useful process-only startup path and keeps repair information available when
a user configuration is damaged.

## Discovery and precedence

The precedence from low to high is:

1. Established application defaults.
2. One discovered or explicitly selected TOML file.
3. Existing compatibility environment, notably `MSYNC_STATE_DIR`.
4. Explicit command-line options.

Discovery checks the platform-conventional user path first:

- Unix: `$XDG_CONFIG_HOME/msync/config.toml`, or
  `$HOME/.config/msync/config.toml` when the XDG variable is unset or empty.
- Windows: `%APPDATA%/msync/config.toml`.
- If the canonical file is absent, `$HOME/.msync/config.toml` (Windows:
  `%USERPROFILE%/.msync/config.toml`) is the legacy/convenient-home fallback.

The canonical and fallback files are alternatives, not implicitly merged layers.
A corrupt canonical file is an actionable error, not permission to silently use
a fallback. Ordinary current-directory `config.toml` files are not auto-loaded:
changing a script's cwd must not implicitly authorize unrelated local policy.

`--config PATH` selects a required file, so a missing file is an error.
`--no-config` bypasses discovery but does not erase explicit CLI options or the
existing `MSYNC_STATE_DIR` environment contract. The two flags conflict.

Relative paths inside a configuration file are anchored to that file's parent
folder. They are not resolved relative to a later script/REPL working directory.
Explicit CLI paths retain ordinary invocation-cwd semantics.

## Core configuration

```toml
[execution]
jobs = 4
max_streams = 16
buffer_size = "64KiB"
memory_limit = "64MiB"
spool_limit = "unlimited"
# state_dir = "state"

[ui]
color = "auto"

[language]
syntax = "0.6"

[progress]
delay_ms = 200
interval_ms = 250
```

`jobs`, `max_streams`, `buffer_size` and `memory_limit` must be positive;
`memory_limit` must admit at least one configured buffer. Byte values accept
integer bytes or `KiB`, `MiB`, and `GiB` strings. `spool_limit` is a per-program
combined stdout/stderr budget; zero is meaningful and `"unlimited"` is the default.
`delay_ms` permits zero; `interval_ms` must be at least one millisecond.
`color` is `auto`, `always`, or `never`, preserving existing `NO_COLOR`/terminal
behavior in auto mode. `0.5` remains a syntax alias; configuration does not
restore removed transactional grammars.

Admission bounds are validated before execution too: `jobs`, `max_streams`, and
the `memory_limit / buffer_size` permit count must fit Tokio's platform semaphore
range. A single buffer must fit the platform allocation range (`isize::MAX`).
These produce keyed configuration errors, not a panic from constructing a runtime
semaphore or allocating an impossible transfer buffer.

The inspection output normalizes byte budgets to integers, except unsigned
budgets above TOML's signed integer range use a quoted decimal string. Both forms
round-trip through the ordinary loader. Path settings resolve to absolute paths.
Unset optional paths are omitted, not serialized as
the filenames `"none"` or `"default"`. This keeps the output reusable as an
ordinary configuration file, including when no home/state location is available.

Existing flags such as `--jobs`, `--max-streams`, `--buffer-size`,
`--memory-limit`, `--spool-limit`, `--color` and `--syntax` override their configured
counterparts. Configuring a small `spool_limit` has actual runtime consequences:
an external program exceeding it fails with an output/spool diagnostic rather
than merely changing the inspection text.

## SQLite telemetry policy

Telemetry is opt-in; configuration inspection and `--no-telemetry` do not create
its destination or start its writer. Relative `path` values retain the same
configuration-directory anchoring as other path settings. A configured SQLite
session can be inspected without write admission using
`msync telemetry summary PATH`.

If enabled telemetry has no explicit path, an actual command creates its
conventional state parent before opening the default database. Inspection,
help/version and explicit disabling do not create that directory. An explicitly
configured custom path retains an ordinary open error when its parent is absent;
the application does not silently substitute a different destination.
SQLite `sample_per_million = 0` skips exporter admission entirely: it neither
requires a home/destination nor creates the default state directory or database,
even when `enabled = true`. Settings are still validated before command execution.

```toml
[telemetry]
enabled = false
backend = "sqlite"
# path = "logs/telemetry.sqlite"
queue_capacity = 4096
batch_size = 128
flush_interval_ms = 100
busy_timeout_ms = 1000
max_sessions = 100
max_records_per_session = 1000000
max_database_bytes = "256MiB"
synchronous_full = false
sample_per_million = 1000000
```

`--telemetry-db FILE` explicitly enables SQLite; the existing `--trace FILE`
selects JSONL instead. These explicit exporters conflict with each other and
with `--no-telemetry`, rather than silently ignoring a requested output.

Queue capacity is bounded to 1–1,000,000 records and batches to 1–65,536 records.
Flush intervals are positive, busy timeouts fit SQLite's signed 32-bit
millisecond interface, and sampling is between zero and 1,000,000 per million.
Database capacity is at least 1 MiB and fits a signed 64-bit count. Configuration
progress/flush durations do not exceed 24 hours, avoiding ineffective or
pathological long waits while keeping intentional slow schedules representable.
Sampling and exporter retention behavior are specified in the
[telemetry contract](telemetry.md), not inferred from configuration text alone.

## Failure contract

Unknown tables/keys, wrong TOML types, malformed syntax, invalid enum values,
negative/overflowing byte counts and cross-field invariant failures are errors.
The diagnostic names the configuration file, relevant key, and source line/column,
retains the parser or native IO cause, and offers a repair. A failed configuration
must not create transfer destinations or runtime staging data. Configuration
input is limited to a regular UTF-8 file of at most 1 MiB; a FIFO cannot turn
startup into a wait for an absent writer. Configuration
errors are not rendered as errors in an unrelated DSL token.

## Focused acceptance

`tests/config_cli_v6.rs` invokes the real CLI with isolated HOME, XDG/APPDATA and
state variables. Its fixtures remain under the repository's `.temp` directory.
It covers default provenance and side-effect-free inspection, canonical versus
fallback selection, explicit overrides, relative paths, environment compatibility,
bypasses, exact source diagnostics, real transfers, and real subprocess spool
limits, configured SQLite paths/disable, exporter flag conflicts, and bounded
timing/resource policies.

The root's native Linux incremental gates passed all **20 distinct configuration
tests** on 2026-10-31:

| Evidence under `.temp/performance-v6/` | Newly accepted tests | Runtime |
| --- | ---: | ---: |
| `features.log`: initial configuration target | 17 | 0.13 s |
| `final-functional.log`: keyed platform resource bounds | 1 | 0.03 s |
| `final-functional.log`: unsigned CLI byte-budget round-trip | 1 | 0.02 s |
| `unit-gates.log`: zero-sampled default destination admission | 1 | 0.03 s |

The platform-bound case rejects oversized semaphore/buffer values at their
configuration keys before runtime admission. The unsigned-budget case preserves
the CLI's full `u64` spool budget and, on 64-bit platforms, memory-budget range
without producing invalid TOML or allocating those budgets. The zero-sampled
case executes a real compile-only command with enabled SQLite, both with and
without a home directory, without admitting any default exporter destination.

The source-key diagnostic journey was additionally rechecked after shared
validation changed (one pass, 0.05 s in `final-functional.log`); that repeat is
not counted as another distinct test. Empty filtered invocations are not counted
as acceptance evidence. These are focused passing increments, **not** a claim
that the entire 20-test target was rerun against one final snapshot, or that
unrelated targets in the same log passed. No configuration journey remains
pending execution.
