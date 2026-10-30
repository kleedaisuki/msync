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
source + CompileContext → parser/provenance → resolved Program
                                      ↓
             snapshot paths + inspect types → optimize to a fixed point
                                      ↓
                       dependency DAG + resource limits
                                      ↓
                 directory expansion → leaf byte flows → OS-selected I/O
```

## Install a release

Download from the [official GitHub Releases](https://github.com/kleedaisuki/msync/releases/latest).

| Platform | Recommended asset | Experience |
| --- | --- | --- |
| Windows 10/11 x64 | `msync-<version>-windows-x86_64-setup.exe` | Per-user setup, no administrator rights; optional User PATH and Start Menu REPL |
| Windows portable | `msync-<version>-windows-x86_64.zip` | Extract and run `bin\msync.exe`; no registry or PATH changes |
| Linux x86-64, glibc 2.35+ | `msync-<version>-linux-x86_64.tar.gz` | Extract and run `bin/msync`; other platforms can build from source |

Windows setup defaults to `%USERPROFILE%\.msync`, with the executable in `bin`.
Restart an already-running terminal application to pick up PATH changes, or use
**msync REPL** in the Start Menu immediately. Check the installation with
`msync --version` and `msync --help`. Close running msync transfers before upgrading.
Uninstall removes packaged files and installer-owned PATH entries, preserving
user configuration, scripts, data and unrelated helpers. The application still
does not load a standalone `~/.msync/config.toml`; SSH profiles remain in OpenSSH's
configuration. Rust and the Visual C++ Redistributable are not required to use
the Windows packages. Optional transport clients are described below.

Windows release files are currently **unsigned**: SmartScreen may show an unknown
publisher warning. Verify the official release origin and compare `Get-FileHash
<download> -Algorithm SHA256` with that release's `SHA256SUMS`; do not disable
system protection. Checksums detect corruption, not a trusted code-signing
identity. Matching GPL source and third-party notices accompany each release.
See [Windows packaging and lifecycle](docs/design/windows-packaging.md) for
upgrade/PATH ownership and reproducible developer packaging commands.

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

### Export and reuse intermediate representations

```sh
msync emit-ir copy.msync -o portable.json
msync bind-ir portable.json --source-file /path/to/copy.msync -o bound.json
msync plan-ir bound.json
msync run-ir bound.json --dry-run
msync run-ir bound.json
```

Relocatable IR resolves binding names but retains symbolic `$pwd`/`$file` roots;
binding supplies a cwd and, when needed, an explicit named-source context.
`emit-ir FILE --ir-stage contextual` exports already-bound local paths instead.
Contextual IR retains those paths across `/cd` or another invocation. Both formats
are versioned JSON, independent of the original source file; every execution
re-inspects endpoints and rebuilds its dependency plan rather than trusting saved
filesystem proofs. Credentials and connected SSH sessions are not serialized.
The existing `compile` command remains an alias for `plan`.

The REPL exports its last prepared transfer with `/export-ir FILE` or
`/export-relocatable FILE`, and reuses an artifact with `/load-ir FILE` (foreground,
Ctrl-C cancels pending flows). Earlier captured path aliases remain captured;
relocation does not undo their binding-time semantics. See [IR binding and format
contract](docs/design/ir.md) for details and API entry points.

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

Only proven ordinary-file assignments qualify for these rewrites. Directories, selectors, container insertions, unknown types, and observable stream effects are not treated as interchangeable scalar writes. Source forwarding does not duplicate dynamic HTTP fetches or replace a materialized remote-to-local snapshot with repeated downloads. Optimization preserves the modeled final file state and dependencies, not intermediate writes or the data reads/errors of eliminated copies. External concurrent observers are not part of that final-state model. See [planning invariants and tests](docs/design/planning.md), [proof obligations/counterexamples](docs/design/planner-proof.md), and [current planner measurements](docs/design/performance-planner-v3.md).

### Bounded resources

```sh
msync run script.msync --jobs 4 --max-streams 8 --buffer-size 64KiB --memory-limit 16MiB
```

| Option | Meaning | Default |
| --- | --- | --- |
| `--jobs N` | Concurrent finite copy operations | 4 |
| `--max-streams N` | Separate background pipe/socket flows | 16 |
| `--buffer-size N` | Transfer buffer size; bytes or KiB/MiB/GiB | 64 KiB |
| `--memory-limit N` | Aggregate declared payload-buffer budget, not process RSS | 64 MiB |

Idle streams do not consume finite-copy worker slots, although both lanes share buffer admission. Dependency edges still serialize conflicting resources. Admission reserves the flow's local payload buffer, participating WSL/SSH helper buffers, and any remote-writer framing buffer; a finite kernel-assisted copy conservatively reserves its possible fallback buffer too. A directory worker expands one leaf at a time and reserves one local buffer, not one per child. Total declared payload buffers stay within the budget; OS socket buffers, framework-owned I/O staging, client/interpreter RSS, fixed diagnostic capture and the rest of the process address space are outside this cap. Configure enough shared capacity for the desired overlap. Zero counts, zero buffers, and budgets unable to admit a flow fail before execution.

Complementary producers and consumers of a channel form a **co-start cohort**, not a completion dependency: `input -> fifo; fifo -> output` must open both flows together. Multi-pipe chains work the same way. The scheduler reserves the entire group's stream slots and buffers atomically; insufficient capacity is diagnosed before any endpoint opens. Ambiguous channel pairings, closed feedback and conflicting file dependencies are diagnosed instead of hanging. See [channel versus completion graphs](docs/design/planning.md).

## Language and path semantics

One statement per nonempty line. `#` begins a comment at a whitespace boundary outside quotes/selectors; adjacent URL fragments remain operand text. Whitespace around operators is optional. Paths and names may contain Unicode.

| Form | Meaning |
| --- | --- |
| `remote => $wsl` | Bind an endpoint |
| `server => ssh://alice@host:2222` | Bind SSH |
| `server => ssh://Production_Box` | Use a native OpenSSH `Host Production_Box` configuration entry |
| `public => https://example.com/` | Bind a read-only HTTP(S) source |
| `wire => tcp://127.0.0.1:9000` | Bind an active TCP stream endpoint; use `wire::/` as its operand |
| `.config := ~/.config` | Bind a path alias |
| `$pwd::pwd/source -> $pwd::pwd/target` | Copy to an exact path; overwrite an existing regular file, reject an existing directory |
| `$pwd::pwd/source -> $pwd::pwd/directory/` | Append the source basename; reject existing files, recursively merge directories without overwriting their files |
| `$pwd::pwd/configs/<.+\.toml> -> $pwd::pwd/output/` | Match complete immediate child basenames; recursively copy a matched directory |

Without a trailing `/`, a missing directory target can be created from a directory source, but an **existing directory target is an error even for a directory source**. Append `/` only when container semantics are intended. Unmentioned children in a merging container remain in place. This is one-way copying and streaming, not deletion, bidirectional reconciliation, or a watch daemon.

Selectors use Rust `regex` syntax and full-basename matching. Backreferences and lookaround are unsupported. A selector is the final source component and needs a destination ending in `/`. Enumeration itself is not recursive; no matches is a successful no-op.

### Context namespaces, endpoint spaces, and path values

A **space is not a directory**. `=>` binds a space or lookup capability; `:=` binds a path value. Context namespaces expose named items rather than treating any text after `::` as a child path:

| Access | Value | Children |
| --- | --- | --- |
| `$pwd::pwd` | Absolute logical cwd captured for this submission | `$pwd::pwd/assets/` |
| `$file::this` | Absolute logical path of the named source script | Rejected: this item is a file |
| `$file::parent` | That script's parent directory | `$file::parent/assets/` |

Use `$pwd::pwd/file` for an explicit cwd-relative expression. The old `$pwd::file`, `$pwd/file`, and bare `$pwd` spellings are intentionally rejected with migration help; `$pwd` has exactly one item, `pwd`. Existing ordinary bare relative paths remain compatible. Formatting is syntax-only and does not guess namespace migrations or silently prefix bare operands that may be aliases.

```text
this => $pwd
captured := $pwd::pwd
```

`this` preserves the namespace: `this::pwd` resolves against each later submission, so it follows REPL `/cd`. `captured` preserves the absolute path value from its declaration and does not move after `/cd`. Namespace-item lookup happens before path-alias lookup; `$pwd::foo` remains an unknown item even if a value alias named `foo` exists.

Named script inputs supply a source identity independently of cwd:

```text
file => $file
assets := file::parent/assets

file::this -> $pwd::pwd/backups/
assets/<.+\.toml> -> $pwd::pwd/copied-assets/
```

`file::this` is available only because `file => $file` explicitly binds that alias; the bare name `file` is not an implicit built-in. `run`, `plan`, `compile`, and `check` with a named file, and REPL `/load`, `/check`, or `/plan FILE`, carry that script's identity. Stdin and direct REPL submissions have no `$file` context and produce an actionable error instead of substituting cwd. `file => $file` itself can be declared before a script is loaded; accessing its items requires an actual named-file submission. A namespace alias does not cache the last loaded script: direct REPL item access still needs a named-file context, while a path value previously captured with `:=` remains usable. File-valued aliases retain provenance transitively, so appending `/child` to an alias of `$file::this` is rejected too.

Source identity is an **absolute logical path**, not a canonicalized filesystem target. Namespace lookup does not read files or resolve symlinks. In the library, `CompileContext { pwd, source_file }` and `compile_with_context` / `compile_rich_with_context` make the input context explicit; the context-free APIs capture cwd but deliberately have no source file.

| Physical endpoint space | Behavior |
| --- | --- |
| `$local` | Local filesystem; `~/` uses HOME or USERPROFILE |
| `$wsl` | Default WSL distribution through `wsl --exec` |
| `$path` | Read-only single filename lookup in PATH, e.g. `$path::gcc.exe` |
| `$env` | First component names an environment variable, e.g. `$env::LOCALAPPDATA/tools/` |
| SSH alias | Remote filesystem or byte stream using native SSH clients |
| HTTP(S) alias | One read-only file downloaded with `curl` |
| TCP alias | One active connection to the declared host and mandatory port |

Spaces can refer to earlier space bindings. Value aliases expand whole first components: `.config/x` expands `.config`, not `.config-old/x`. An unqualified path-only alias such as `.config := ~/.config` remains endpoint-relative and works with both `.config/x` and `remote::.config/x`. A value obtained from a contextual namespace is explicitly local and cannot be requalified as WSL/SSH. Failed frontend compilation commits none of that submission's declarations.

`~` means the current endpoint user's home, not `~another-user`. On HTTP, `~/` and leading `/` are endpoint-relative prefixes; a base ending in `/` appends below that directory. HTTP credentials belong in client configuration. Binding names contain letters, digits, `_`, `-`, or `.`, but cannot be `.` or `..`.

### Quoting and formatting scripts

Quote the entire operand when it contains whitespace, a comment delimiter, or an operator:

```text
"$pwd::pwd/source files/config.toml" -> "$pwd::pwd/output files/config.toml"
"C:\\Users\\Klee\\config.toml" -> $pwd::pwd/output/config.toml
```

Inside double quotes, `\\`, `\"`, `\n`, `\r`, and `\t` are escapes. Unknown escapes retain their backslash. Use `/` or doubled backslashes in quoted Windows paths; unquoted backslashes are literal. The portable directory-mode marker is a trailing **forward slash**.

```sh
msync fmt script.msync             # Format this script file in place
msync fmt script.msync --check     # Read-only check; nonzero if formatting differs
msync fmt script.msync --stdout    # Explicit read-only canonical preview
```

`--write` remains an alias for the default in-place behavior. `fmt --dry-run` is rejected with guidance to use `--check` or `--stdout`, never silently ignored while rewriting a script; dry-run previews transfer plans. Default `fmt -` is rejected rather than treating formatting as a transfer between stdin/stdout. The pure library formatter still returns text, and explicit preview can read stdin. The formatter preserves comments and operand values, canonicalizes spacing/quoting, ends nonempty source with a newline, and does not retain blank-line layout. In-place writes use exclusive temporary-file creation and refuse symlinks/read-only inputs.

## Pipes and sockets

Every non-directory transfer is a source-to-sink byte flow: ordinary files, FIFO/named pipes, sockets, HTTP responses and remote endpoints participate through adapters. Directories are structural: their entries expand into child flows instead of sending directory bytes to a socket. A directory cannot be sent to a stream. A named source can use a container destination with the normal no-clobber contract; an unnamed TCP source needs an exact output filename.

```text
wire => tcp://127.0.0.1:9000
$pwd::pwd/input.bin -> wire::/
```

TCP endpoints actively connect; msync is not implicitly a listening server. WSL/SSH adapters used by asynchronous socket/IPC relays additionally require Python 3 for the bounded nonblocking remote helper; the specialized ordinary remote filesystem-copy path does not. Native Windows named-pipe paths use `\\.\pipe\name`; Unix uses FIFO or Unix-domain socket paths. Transfers are asynchronous and backpressured with bounded buffers. Source EOF or a closed destination ends **that flow only**, including when the source is idle. This is a one-way protocol: destination TCP/Unix EOF/FIN, including a sending-half shutdown, is a logical end-of-flow; reverse bytes are discarded by a bounded close watcher. Other independent tasks continue, and the process waits until its remaining flows finish. Ctrl-C cancels stream waits and closes their handles. Cancellation is cooperative: asynchronous waits close and supported native operations receive cancellation; an operation that cannot be interrupted must drain before its owned handles are released. See [stream transport details](docs/design/streams.md) for platform requirements and remote-helper behavior.

## Uniform I/O and file publication

`->` describes a byte flow, not two unrelated APIs for "copy" and "IPC". The shared `io` layer owns progress/end states, cancellation, staged file publication and OS capability selection. Ordinary-file pairs may use a kernel-assisted bulk path; pipe/socket/HTTP/remote adapters retain asynchronous backpressure. Directories expand structurally into child flows and are not serialized as directory bytes.

| Workload | Capability-selected implementation |
| --- | --- |
| Native file → file | Linux `copy_file_range` or Windows `CopyFileExW`, with a bounded fallback where needed |
| Native file → plain TCP | Linux `sendfile`; Windows `TransmitFile` for at least 1 MiB remaining, buffered below that measured cutoff; readiness/completion and cancellation are preserved |
| Other byte-flow pairs | Bounded asynchronous source/sink adapters over the same semantic flow contract |

A fast-path fallback resumes the accepted prefix on the **same open file/connection**; it must not reconnect or retransmit already delivered bytes. OS event integration is Tokio's epoll/IOCP machinery plus the selected operation's readiness/completion contract. The optional remote Python helper is a transport adapter, not the application's event-loop runtime.

Native finite ordinary-file outputs are copied into an exclusively owned sibling stage and published only after successful completion. Before publication, an error or cancellation removes the uncommitted stage: an existing target remains unchanged and a new target remains absent. No-clobber publication is atomic. Assignment replaces the destination **pathname binding**, not every hardlink to its old inode: other hardlink aliases retain their old content. This differs from an in-place overwrite and is necessary for the per-file rollback contract and already queued zero-copy source pages. A directory transfer is still a sequence of child-file commits: earlier successful leaves and created directories remain after a later failure. This is per-file protection, not a whole-tree transaction or a crash-durable commit promise. The individual sink/transport note records the corresponding stream and remote guarantees.

`io_uring` and Windows I/O Ring capability probes and workload measurements are separate from production-path selection. A successful probe is not a claim that every transfer uses a ring, nor that rings outperform the simpler path on the relevant workload. The [Linux investigation](docs/design/io-linux-v3.md) separates native WSL ext4, tmpfs and Windows-backed DrvFs, including matched queue-depth/concurrency controls and verified Linux page-cache residency. Deep rings improved some large single-flow DrvFs cases, but not the native ext4 copy workload; neither a depth-one tmpfs test nor a cross-OS mount result represents all Linux storage. The [Windows investigation](docs/design/io-windows-v3.md) records actual ring operations and native transfer thresholds. Production keeps the selected kernel-copy/readiness paths rather than adding an unvalidated universal ring engine. Read the OS-specific notes before changing dispatch defaults; kernel-assisted file transfer does not make SSH encryption or arbitrary adapters universally zero-copy.

## REPL

```text
msync> /?
msync> root := $pwd::pwd/configs
msync> /dry-run on
msync> root/app.toml -> $pwd::pwd/output/app.toml
msync> /limits
msync> /quit
```

| Command | Action |
| --- | --- |
| `/help`, `/?` | Highlighted command help |
| `/clear` | Clear the terminal viewport; keep bindings, in-memory history and jobs |
| `/bash [COMMAND]` | Enter Bash, or run one raw shell command and return; Windows uses WSL Bash |
| `/pwsh [COMMAND]` | Enter PowerShell 7, or run one raw PowerShell command and return |
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

In a supported interactive terminal, v0.3.1 uses a native line editor: namespaces,
operators, strings, selectors and comments are highlighted **while typing**, even
before a statement is complete. Tab completes command names and built-in namespace
items; Up/Down recall this session's history. Ctrl-C discards an unfinished edit
and stops an active plan without exiting; Ctrl-D exits safely. History stays in
memory, never an automatic plaintext history file. Leading-space inputs are not
added to history. `--color never` disables colors, not editing, history or Tab.
Unsupported/dumb terminals use plain basic input instead of pretending to support
cursor editing. Piped input retains its line-oriented automation behavior.

`/clear` is not `/reset`: it does not erase bindings, history, jobs or scrollback.
If viewport clearing is unavailable, it explains that without printing terminal
escape commands into a pipe. Shell entry without a command requires a terminal;
one-shot commands work with piped REPL input and cannot consume subsequent DSL
lines. The command tail is passed unchanged to Bash `-c` or PowerShell `-Command`,
not parsed as DSL. For example, `/bash printf '%s' 'a -> b'` is a shell command.
Child `cd`/environment changes do not alter msync's cwd or DSL bindings. A shell
can still modify real files, so `/bash` and `/pwsh`, like `/cd` and `/fmt`, require
`/wait` or `/stop` while a plan is active. Interactive children retain their normal
profiles/history; PowerShell one-shot commands use `-NoProfile -NonInteractive`.

## Diagnostics and telemetry

Diagnostics retain operand/operator spans and decoded-to-original byte mappings, so an escaped quoted regex or unknown endpoint highlights the actual source token rather than a whole line. Structured reports include primary spans, secondary labels and constructive help, including likely endpoint spellings and explicit slash/path repairs. Tabs, CJK widths and combining characters align safely; terminal control characters are escaped. Help, REPL help, diagnostics, prompts and status use actual ANSI styling on supported terminals, with Windows console adaptation through `anstream`. Auto mode respects pipes, `TERM=dumb` and `NO_COLOR`; explicit `--color always` or `--color never` wins.

Missing scripts and filesystem failures separate the attempted action, affected
path, relative-path cwd, original OS cause and a useful repair. Data-source errors
also show the expanded endpoint, not only its alias token. Rich source diagnostics
use one error heading, a named file/line location and grouped source rows with
distinct primary/secondary markers. Interactive plans separate `from`, `to`,
endpoint types and dependencies; piped plans keep their established plain format.
Formatting `--stdout` and telemetry JSONL remain exact payloads, not decorated UI.
See [the v0.3.1 interaction design](docs/design/cli-ux-v0.3.1.md).

```sh
msync run script.msync --trace .temp/run.jsonl
msync check script.msync --trace -
```

Create the trace parent directory first. Telemetry is **off by default, local-only, and never uploaded**. One session trace connects parsing, compilation, preparation, optimization, scheduling and copy/stream tasks. Explicit async context propagation preserves parents across Tokio tasks and blocking workers without holding thread-local guards across `.await`. Records retain useful paths, backend/phase choices, typed causes, IDs, durations and progress; they do not automatically capture file payloads or unrelated environment/credential snapshots. Versioned JSONL is not an OTLP wire exporter; export failures are visible and spans flush before CLI exit. See [the telemetry contract](docs/design/telemetry.md).

## Safety, platforms and library use

- Preparation validates known types before executing the graph. Each regular-copy operation preflights its selected trees, rejecting same-file/hard-link identity, overlapping directories in either direction, symlinks/reparse points and unsupported device entries. Insert-mode file creation is atomic no-clobber.
- Independent nodes may execute concurrently; a later failure can coexist with completed or in-flight writes. There is no whole-script filesystem rollback, crash-durable transaction, or defense against hostile concurrent parent/source replacement.
- HTTP requires `curl`; WSL requires a distribution; SSH requires native `ssh`, modern SFTP-default `scp`, and external authentication. `MSYNC_SSH_CONFIG` selects a separate client file for both clients via `-F` without editing existing SSH settings. No persistent SSH service/account/config is installed by ordinary use or the isolated fixture.
- Remote ordinary copies require POSIX `sh` and GNU-compatible filesystem helpers. Native Windows download validation rejects unrepresentable names, device aliases and conservative uppercase path collisions before SCP. Secrets and staging stay private; transport scratch data is RAII-cleaned under local `.temp`, with private remote staging when needed.
- Permissions are copied where supported; ACLs, ownership, timestamps, alternate streams and every platform-specific attribute are not a portable contract.

```rust
use msync::{compiler::{CompileContext, Compiler}, scheduler, syntax};

let source = "$pwd::pwd/configs/app.toml -> $pwd::pwd/output/app.toml\n";
let ast = syntax::parse(source).expect("valid syntax");
let context = CompileContext::capture(None).expect("cwd snapshot");
let program = Compiler::new().compile_with_context(&ast, &context).expect("valid declarations");
let plan = scheduler::prepare(&program, false).expect("offline plan");
assert_eq!(plan.nodes.len(), 1);
```

For real execution, use live preparation (`scheduler::prepare(&program, true)`) and execute the resulting graph with `scheduler::execute_plan` and `ResourceLimits`; the example above is an offline preview. Execution rejects uninspected `Unknown` file classes before I/O, with guidance to prepare live. A child whose kind is deferred until an earlier finite directory producer completes is allowed; its runtime adapter rechecks the actual filesystem before copying. `Missing` is different: an earlier graph operation may legitimately produce that resource. Inside an existing Tokio runtime use `execute_plan_async` with a cancellation token; the synchronous wrapper refuses nested-runtime use instead of panicking. `runtime::execute` retains its original signature as a compatibility entrypoint and now routes through planning/scheduling. Basic diagnostics and pure `syntax::format` APIs remain available alongside rich reports and CLI in-place formatting.

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --all-targets
cargo test --test runtime -- --ignored --test-threads=1
cargo test --test ssh -- --ignored --nocapture --test-threads=1
cargo bench --bench pipeline
cargo bench --bench planner
```

Tests exercise CLI/REPL journeys, graph state equivalence and fixed-point properties, bounded scheduling, real TCP/named-pipe I/O, local filesystems, HTTP, and telemetry parent graphs. The explicit SSH suite uses Windows OpenSSH plus a privately configured, chrooted WSL daemon; it does not modify existing SSH configuration. Platform-specific and optional remote-stream verification is recorded in [validation](docs/design/validation.md), not inferred from another OS's results.

Design references: [architecture](docs/design/implementation.md), [planning](docs/design/planning.md), [syntax](docs/design/syntax.md), [runtime/SSH](docs/design/runtime.md), [streams](docs/design/streams.md), [Linux I/O](docs/design/io-linux-v3.md), [Windows I/O](docs/design/io-windows-v3.md), [telemetry](docs/design/telemetry.md), and [earlier compiler CPU performance](docs/design/performance.md). Earlier compiler microbenchmarks are not file/network throughput claims. The [indexed-planner report](docs/design/performance-planner-v3.md) measures actual Plan construction separately and records its allocation/retained-memory measurements, tiny-script controls and comparison limits. The [startup report](docs/design/performance-startup-v3.md) measures complete CLI/REPL journeys and actual remote metadata waits in milliseconds rather than inferring user experience from parser throughput.

License: GPL-3.0 (see LICENSE).
