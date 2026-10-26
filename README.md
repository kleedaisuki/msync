# msync

A typed synchronization DSL in Rust: source-mapped diagnostics, an optimizing dependency-graph planner, bounded file/stream execution, a comment-preserving script formatter, and a persistent REPL.

```text
remote => $wsl
.codex := ~/.codex

.codex/config.toml -> remote::.codex/config.toml
.codex/agents/<.+\.toml> -> remote::.codex/agents/
```

The supplied [`spread.msync`](docs/dsl/spread.msync) is the original semantic seed. The implementation now separates **Plan → Execute**; compilation does not mean native machine-code generation.

```text
source → parser + operand provenance → resolved Program
                                      ↓
             snapshot paths + inspect types → optimize to a fixed point
                                      ↓
                       dependency DAG + resource limits
                                      ↓
                 bounded copies + asynchronous byte-stream flows
```

## Build and run

```sh
cargo build --release
cargo run -- check docs/dsl/spread.msync
cargo run -- plan docs/dsl/spread.msync
cargo run -- run docs/dsl/spread.msync --dry-run
cargo run -- fmt docs/dsl/spread.msync --check
cargo run -- repl --dry-run
```

The executable is `target/release/msync` (`msync.exe` on Windows). `msync SCRIPT` means `msync run SCRIPT`; no arguments starts the REPL. `--help` lists commands and resource controls. Pass `-` to script-reading commands for UTF-8 stdin; formatting edits a named file by default.

**`run` and REPL transfer statements write real data unless `--dry-run` is enabled.** The seed references personal configuration directories; inspect its plan before executing it.

## Plan, optimize, execute

| Command | Contract |
| --- | --- |
| `msync check FILE` | Parse and type-check declarations without copying or inspecting remote files |
| `msync plan FILE` | Snapshot local path context, inspect endpoint metadata, optimize, and print the dependency graph; never copies file data |
| `msync compile FILE` | Alias for `plan` |
| `msync run FILE` | Prepare the optimized plan, then execute its dependency graph |
| `msync run FILE --dry-run` | Offline plan/preview: no writes, remote clients, or network requests; unknown remote types remain conservative |

Planning may contact WSL/SSH to classify a path, but does not open FIFO/socket data or start a transfer. Offline previews cannot make every optimization that live metadata enables. `--trace FILE` explicitly writes telemetry even during a dry-run.

### The graph is the executable model

```text
config_file := ~/.codex/config.toml
des := ~/Desktop

config_file -> des/config.toml
$wsl::config_file -> des/config.toml
```

When metadata establishes ordinary-file assignments, the first write is dead: only the final source needs copying. An intervening read of the destination prevents blindly discarding its producer. The planner performs conservative source forwarding and dead-store elimination until no rewrite applies, then builds read-after-write, write-after-read, write-after-write, and consuming-stream dependencies. Plan output retains elimination/forwarding reasons and original operation provenance.

Only proven ordinary-file assignments qualify for these rewrites. Directories, selectors, container insertions, unknown types, and observable stream effects are not treated as interchangeable scalar writes. Source forwarding does not duplicate dynamic HTTP fetches or replace a materialized remote-to-local snapshot with repeated downloads. Optimization preserves the modeled final file state and dependencies, not intermediate writes or the data reads/errors of eliminated copies. External concurrent observers are not part of that final-state model. See [planning invariants and tests](docs/design/planning.md).

### Bounded resources

```sh
msync run script.msync --jobs 4 --max-streams 8 --buffer-size 64KiB --memory-limit 16MiB
```

| Option | Meaning | Default |
| --- | --- | --- |
| `--jobs N` | Concurrent finite copy operations | 4 |
| `--max-streams N` | Separate background pipe/socket flows | 16 |
| `--buffer-size N` | Transfer buffer size; bytes or KiB/MiB/GiB | 64 KiB |
| `--memory-limit N` | Aggregate explicit relay-buffer budget, not process RSS | 64 MiB |

Idle streams do not consume finite-copy worker slots. Dependency edges still serialize conflicting resources. Each flow reserves its local relay buffer plus one buffer for each participating WSL/SSH helper. The budget does not promise to limit OS socket buffers, client processes, interpreter memory, or the entire process address space. Zero counts, zero buffers, and budgets unable to admit a flow fail before execution.

Complementary producers and consumers of a channel form a **co-start cohort**, not a completion dependency: `input -> fifo; fifo -> output` must open both flows together. Multi-pipe chains work the same way. The scheduler reserves the entire group's stream slots and buffers atomically; insufficient capacity is diagnosed before any endpoint opens. Ambiguous channel pairings, closed feedback and conflicting file dependencies are diagnosed instead of hanging. See [channel versus completion graphs](docs/design/planning.md).

## Language and path semantics

One statement per nonempty line. `#` begins a comment at a whitespace boundary outside quotes/selectors; adjacent URL fragments remain operand text. Whitespace around operators is optional. Paths and names may contain Unicode.

| Form | Meaning |
| --- | --- |
| `remote => $wsl` | Bind an endpoint |
| `server => ssh://alice@host:2222` | Bind SSH |
| `public => https://example.com/` | Bind a read-only HTTP(S) source |
| `wire => tcp://127.0.0.1:9000` | Bind an active TCP stream endpoint; use `wire::/` as its operand |
| `.config := ~/.config` | Bind a path alias |
| `$pwd::source -> $pwd::target` | Copy to an exact path; overwrite an existing regular file, reject an existing directory |
| `$pwd::source -> $pwd::directory/` | Append the source basename; reject existing files, recursively merge directories without overwriting their files |
| `$pwd/configs/<.+\.toml> -> $pwd/output/` | Match complete immediate child basenames; recursively copy a matched directory |

Without a trailing `/`, a missing directory target can be created from a directory source, but an **existing directory target is an error even for a directory source**. Append `/` only when container semantics are intended. Unmentioned children in a merging container remain in place. This is one-way copying and streaming, not deletion, bidirectional reconciliation, or a watch daemon.

Selectors use Rust `regex` syntax and full-basename matching. Backreferences and lookaround are unsupported. A selector is the final source component and needs a destination ending in `/`. Enumeration itself is not recursive; no matches is a successful no-op.

### Explicit working-directory paths

Prefer `$pwd::file`, `$pwd/file`, or bare `$pwd` for local working-directory paths. The current directory is snapshotted during planning; in the REPL it follows `/cd`. Existing bare relative paths remain supported for compatibility, but new examples use explicit `$pwd`. Formatting does not silently prefix every bare path: an apparent relative operand may be a path-polymorphic alias.

| Endpoint | Behavior |
| --- | --- |
| `$pwd`, `$local` | Local filesystem; `~/` uses HOME or USERPROFILE |
| `$wsl` | Default WSL distribution through `wsl --exec` |
| `$path` | Read-only single filename lookup in PATH, e.g. `$path::gcc.exe` |
| `$env` | First component names an environment variable, e.g. `$env::LOCALAPPDATA/tools/` |
| SSH alias | Remote filesystem or byte stream using native SSH clients |
| HTTP(S) alias | One read-only file downloaded with `curl` |
| TCP alias | One active connection to the declared host and mandatory port |

Endpoint declarations can refer to earlier bindings. Aliases expand whole first components: `.config/x` expands `.config`, not `.config-old/x`. Prefix substitution is resolved at declaration time; concrete local cwd/home/environment/PATH resolution is snapshotted when preparing the plan. An unqualified alias such as `.config := ~/.config` remains endpoint-relative and works with both `.config/x` and `remote::.config/x`. An explicitly local `$pwd` alias cannot be requalified as WSL/SSH; a rich diagnostic labels both conflicting sites. Redefinition is allowed, but failed frontend compilation rolls back the entire submission's declarations.

`~` means the current endpoint user's home, not `~another-user`. On HTTP, `~/` and leading `/` are endpoint-relative prefixes; a base ending in `/` appends below that directory. HTTP credentials belong in client configuration. Binding names contain letters, digits, `_`, `-`, or `.`, but cannot be `.` or `..`.

### Quoting and formatting scripts

Quote the entire operand when it contains whitespace, a comment delimiter, or an operator:

```text
"$pwd/source files/config.toml" -> "$pwd/output files/config.toml"
"C:\\Users\\Klee\\config.toml" -> $pwd/output/config.toml
```

Inside double quotes, `\\`, `\"`, `\n`, `\r`, and `\t` are escapes. Unknown escapes retain their backslash. Use `/` or doubled backslashes in quoted Windows paths; unquoted backslashes are literal. The portable directory-mode marker is a trailing **forward slash**.

```sh
msync fmt script.msync             # Format this script file in place
msync fmt script.msync --check     # Read-only check; nonzero if formatting differs
msync fmt script.msync --stdout    # Explicit read-only canonical preview
```

`--write` remains an alias for the default in-place behavior. Default `fmt -` is rejected rather than treating formatting as a transfer between stdin/stdout. The pure library formatter still returns text, and explicit preview can read stdin. The formatter preserves comments and operand values, canonicalizes spacing/quoting, ends nonempty source with a newline, and does not retain blank-line layout. In-place writes use exclusive temporary-file creation and refuse symlinks/read-only inputs.

## Pipes and sockets

The execution model distinguishes ordinary files, directories, and byte streams (FIFO/named pipes and stream sockets). A directory cannot be sent to a stream. A non-directory source can flow to a pipe/socket, and streams can flow into exact regular-file targets or another stream. A named source can use a container destination with the normal no-clobber contract; an unnamed TCP source needs an exact output filename.

```text
wire => tcp://127.0.0.1:9000
$pwd/input.bin -> wire::/
```

TCP endpoints actively connect; msync is not implicitly a listening server. WSL/SSH endpoints participating in a stream flow additionally require Python 3 for the bounded nonblocking remote helper; ordinary remote file copies do not. Native Windows named-pipe paths use `\\.\pipe\name`; Unix uses FIFO or Unix-domain socket paths. Transfers are asynchronous and backpressured with bounded buffers. Source EOF or a closed destination ends **that flow only**, including when the source is idle. This is a one-way protocol: destination TCP/Unix EOF/FIN, including a sending-half shutdown, is a logical end-of-flow; reverse bytes are discarded by a bounded close watcher. Other independent tasks continue, and the process waits until its remaining flows finish. Ctrl-C cancels stream waits and closes their handles. Already launched native blocking copies drain rather than being unsafely aborted. See [stream transport details](docs/design/streams.md) for platform requirements and remote-helper behavior.

## REPL

```text
msync> /?
msync> root := $pwd/configs
msync> /dry-run on
msync> root/app.toml -> $pwd/output/app.toml
msync> /limits
msync> /quit
```

| Command | Action |
| --- | --- |
| `/help`, `/?` | Highlighted command help |
| `/pwd`, `/cd PATH` | Inspect/change the working directory |
| `/bindings`, `/reset` | Inspect/clear user bindings |
| `/dry-run [on\|off]` | Inspect/change offline preview mode |
| `/limits` | Show copy/stream/buffer resource limits |
| `/jobs`, `/wait`, `/stop` | Inspect, wait for, or cancel/join the active background plan |
| `/load FILE` | Compile, plan and execute in the current environment |
| `/check FILE`, `/plan FILE` | Check or inspect a plan without committing new bindings |
| `/fmt FILE` | Format the script file in place; never execute it |
| `/quit`, `/exit` | Exit |

Each input line is a submission; `/load` prepares a multiline script as one declaration transaction. Bindings commit only after preparation succeeds; execution failure cannot roll completed writes back. A streaming plan runs in the background, so `/help`, `/jobs` and other read-only commands remain responsive. One active plan per session preserves a single global resource budget and prevents undeclared cross-submission races: new copy plans, `/cd` and `/fmt` require `/wait` or `/stop` first. Pure declarations can still update future binding snapshots. `/quit` and interactive EOF cancel and join the active plan; piped input EOF waits for its remaining flows. Completed errors retain their original source for rich diagnostics. Piped REPL input suppresses prompts, and colon aliases remain accepted. No unmanaged daemon is created.

## Diagnostics and telemetry

Diagnostics retain operand/operator spans and decoded-to-original byte mappings, so an escaped quoted regex or unknown endpoint highlights the actual source token rather than a whole line. Structured reports include primary spans, secondary labels and constructive help, including likely endpoint spellings and explicit slash/path repairs. Tabs, CJK widths and combining characters align safely; terminal control characters are escaped. Help, REPL help, diagnostics, prompts and status use actual ANSI styling on supported terminals, with Windows console adaptation through `anstream`. Auto mode respects pipes, `TERM=dumb` and `NO_COLOR`; explicit `--color always` or `--color never` wins.

```sh
msync run script.msync --trace .temp/run.jsonl
msync check script.msync --trace -
```

Create the trace parent directory first. Telemetry is **off by default, local-only, and never uploaded**. One session trace connects parsing, compilation, preparation, optimization, scheduling and copy/stream tasks. Explicit async context propagation preserves parents across Tokio tasks and blocking workers without holding thread-local guards across `.await`. Records contain IDs, durations, structural outcomes and numeric metrics, not source, paths, environment values or error bodies. Versioned JSONL is not an OTLP wire exporter; export failures are visible and spans flush before CLI exit. See [the telemetry contract](docs/design/telemetry.md).

## Safety, platforms and library use

- Preparation validates known types before executing the graph. Each regular-copy operation preflights its selected trees, rejecting same-file/hard-link identity, overlapping directories in either direction, symlinks/reparse points and unsupported device entries. Insert-mode file creation is atomic no-clobber.
- Independent nodes may execute concurrently; a later failure can coexist with completed or in-flight writes. There is no whole-script filesystem rollback, crash-durable transaction, or defense against hostile concurrent parent/source replacement.
- HTTP requires `curl`; WSL requires a distribution; SSH requires native `ssh`, modern SFTP-default `scp`, and external authentication. `MSYNC_SSH_CONFIG` selects a separate client file for both clients via `-F` without editing existing SSH settings. No persistent SSH service/account/config is installed by ordinary use or the isolated fixture.
- Remote ordinary copies require POSIX `sh` and GNU-compatible filesystem helpers. Native Windows download validation rejects unrepresentable names, device aliases and conservative uppercase path collisions before SCP. Secrets and staging stay private; transport scratch data is RAII-cleaned under local `.temp`, with private remote staging when needed.
- Permissions are copied where supported; ACLs, ownership, timestamps, alternate streams and every platform-specific attribute are not a portable contract.

```rust
use msync::{compiler::Compiler, scheduler, syntax};

let source = "$pwd/configs/app.toml -> $pwd/output/app.toml\n";
let ast = syntax::parse(source).expect("valid syntax");
let program = Compiler::new().compile(&ast).expect("valid declarations");
let plan = scheduler::prepare(&program, false).expect("offline plan");
assert_eq!(plan.nodes.len(), 1);
```

Execute a prepared graph with `scheduler::execute_plan` and `ResourceLimits`. Inside an existing Tokio runtime use `execute_plan_async` with a cancellation token; the synchronous wrapper refuses nested-runtime use instead of panicking. `runtime::execute` retains its original signature as a compatibility entrypoint and now routes through planning/scheduling. Basic diagnostics and pure `syntax::format` APIs remain available alongside rich reports and CLI in-place formatting.

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --all-targets
cargo test --test runtime -- --ignored --test-threads=1
cargo test --test ssh -- --ignored --nocapture --test-threads=1
cargo bench --bench pipeline
```

Tests exercise CLI/REPL journeys, graph state equivalence and fixed-point properties, bounded scheduling, real TCP/named-pipe I/O, local filesystems, HTTP, and telemetry parent graphs. The explicit SSH suite uses Windows OpenSSH plus a privately configured, chrooted WSL daemon; it does not modify existing SSH configuration. Platform-specific and optional remote-stream verification is recorded in [validation](docs/design/validation.md), not inferred from another OS's results.

Design references: [architecture](docs/design/implementation.md), [planning](docs/design/planning.md), [syntax](docs/design/syntax.md), [runtime/SSH](docs/design/runtime.md), [streams](docs/design/streams.md), [telemetry](docs/design/telemetry.md), and [earlier compiler CPU performance](docs/design/performance.md). Earlier compiler microbenchmarks are not file/network throughput claims.

License: GPL-3.0 (see LICENSE).
