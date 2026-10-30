# msync

A typed file-synchronization DSL in Rust: a pure optimizing compiler, a
block-at-a-time interpreter, bounded asynchronous execution, source-mapped
errors, a script formatter, and a persistent REPL.

> This document describes the **v0.5 breaking-update contract**. Integration and
> release verification are in progress; it does not certify an installed binary.
> The transaction model is removed, not silently emulated.

Syntax **0.5 is the only CLI grammar**. An optional `# msync: 0.5` header or
`--syntax 0.5` makes that explicit. Syntax 0.3 and transactional syntax 0.4 are
rejected with migration guidance, not accepted with silently changed semantics.
To migrate an old quoted filename, use an explicit joined or qualified path,
such as `$pwd / "file name"`; bare `"file name"` is literal data. All scripts
use the same statement publication/recovery owner.

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

Runtime staging normally uses the workspace's `.temp`. If creation is denied
by filesystem permissions, it can use `~/.msync/state` instead; set
`MSYNC_STATE_DIR` to select that stable fallback. Recovery checks configured
state before new effects. An occupied/invalid path is an error, not permission
for silent fallback. This is runtime state, not a new configuration-file loader.

## Example and values

```text
macro copy_codex_config(codex, des):
    codex/config.toml -> des/config.toml
    codex/agents -> des/agents/.

codex := ~/.codex
copy_codex_config($local::codex, $pwd::local)
copy_codex_config($wsl::codex, $pwd::wsl)
```

`:=` binds a typed value; `=>` binds an endpoint, namespace, or program.
`/` joins paths, and `::` selects an item from a namespace. A quoted value is
UTF-8 data, not a filename inferred from its position or current existence:

```text
"hello, 可莉" -> message.txt
$pwd / "input file.txt" -> $pwd / "copied file.txt"
```

Path components may contain internal spaces but not leading/trailing whitespace.
Literal content is preserved byte-for-byte. Macros expand typed values in an
isolated lexical environment. `global name` imports a binding from the macro's
definition-time environment; caller locals are not implicitly captured.

## Reusable IR commands

`emit-ir`, `bind-ir`, `plan-ir` and `run-ir` remain available. New exports contain
expanded 0.5 typed object IR, including macros, literals, programs and barriers;
they never depend on re-reading the original script. `emit-ir FILE -o portable.json`
keeps context roots symbolic. `bind-ir portable.json -o bound.json` or
`emit-ir FILE --ir-stage contextual` captures local path bindings. Runtime file
observations and optimization proofs are always rebuilt. Use `--source-file FILE`
when binding a portable module that accesses `$file`.

The REPL retains `/export-ir FILE`, `/export-relocatable FILE`, and `/load-ir FILE`.
Version-1 artifacts exported by 0.4 retain their original explicit execution
semantics; they are not reinterpreted as 0.5 source. `compile FILE -o module.mobj`
also exposes the language branch's native relocatable object format.
See [IR contract](docs/design/ir.md) and [0.5 integration](docs/design/integration-v0.5.md).

## Compile → interpret → schedule

```text
source + lexical context
          ↓
parse → typed macro expansion → semantic IR
          ↓
ordered basic blocks + per-block dependency graphs
          ├── check / plan / compile / object export
          ↓
interpreter takes the next block
          ↓
current observations → guarded machine-specific optimization
          ↓
bounded graph scheduling → physical IO / program launch
          ↓
block boundary → contextualize the next block
```

A compiler cannot know whether an external program will create, delete, or
replace a later input. It does not inspect the whole script's filesystem to
guess. It records logical operands, effects, barriers, and runtime obligations.
The interpreter evaluates them for the current block only. A failed optimization
guard falls back to unexecuted semantic operations; completed IO is not replayed.

Blocks execute in order. Independent operations **inside** a block may overlap
or reorder when the graph proves this preserves semantics. An explicitly
asynchronous program may remain alive across later blocks; its launch boundary
does not imply its external effects have finished.

There are no DSL transactions, isolation levels, named origins, `commit`, or
`rollback`. Earlier successful statements are not undone by a later failure.
See [barrier compilation](docs/design/barrier-execution.md) and
[runtime design](docs/design/barrier-runtime.md).

## Programs and barriers

Calls use an exact argv vector, never implicit shell reparsing:

```text
Program => $path::bash("-c", "ls")
Program::stdout -> stdout.txt
```

The first line is shorthand for
`async await Program => $path::bash("-c", "ls")`: launch through the asynchronous
runtime and wait for that statement's completion. Explicit `async` permits
continued interpretation after launch:

```text
async Program => $path::bash("-c", "ls")
Program::stdout -> stdout.txt
await Program
```

| Form | Boundary |
| --- | --- |
| `async STATEMENT` | Earlier work cannot move after its start boundary |
| `await STATEMENT` | Later work cannot move before its completion boundary |
| `async await STATEMENT` | Both boundaries |
| `await Program` | Wait for that program and connected port work |
| `await` | Wait for outstanding work |
| `async` | Standalone start-side ordering boundary |

These are directional barriers, not syntax that must be paired. The compiler
inserts a wait at macro exit for asynchronous programs started in that macro and
not already awaited. A macro does not silently leak an unfinished local child.

`async` still allows later independent work to move into an earlier block;
`await` still allows earlier independent work to move into a later block. The
compiler normalizes these one-way constraints and removes unnecessary blocks
before building dependency graphs. Real launches and joins remain observation
anchors; data dependencies and possible aliases still constrain execution.

For a program's newly created files, wait for that program before reading them.
`async` alone guarantees launch ordering, not file readiness:

```text
Generator => $path::bash("-c", "printf generated > generated.txt")
generated.txt -> copied.txt
```

The implicit wait finishes `Generator` first; only then does the interpreter
inspect `generated.txt`. With an explicit async launch, place `await Generator`
before the file read instead of relying on process timing.

Program ports are `stdin`, `stdout`, `stderr`, `pid`, `exit_code`, and `meta`.
Output drains run concurrently with the child, avoiding full-pipe deadlocks,
and remain available to later consumers. Nonzero exit status is data, not
automatically a transport error. Programs run in the actual environment:
Sandboxie, bubblewrap, and a host driver are **not required**. Program files,
network requests, and other external side effects are not rolled back.

Normal cancellation stops new admissions, cancels/drains flows, and retains
started programs until they exit themselves. `/stop` and `/quit` do not kill a
child halfway through its work. They may wait for a nonterminating child. Forced
external termination and power loss are outside this lifetime guarantee. EOF
ends that transfer only, not unrelated work.

REPL `/kill` is an explicit destructive escape hatch: it force-terminates owned
programs and can leave their external effects partial. It is available when the
REPL accepts input with background work active, not while a foreground graceful
drain is blocking input. A second Ctrl-C does not silently become a force kill;
an OS-level forced termination has the same external-effect exclusion.
For WSL and SSH, force-stopping the local transport does not prove that every
guest/server-side descendant has terminated; remote termination is best effort.

## Statement publication and streams

A finite file statement owns its stages and recovery state, not script-wide
transaction history. The implemented publication contract is **one complete
statement or none after recovery**; final acceptance gates are recorded
separately. A failed later statement leaves earlier successful statements
intact. Selectors and container merges still form one statement; multiple
physical renames are not one OS-wide atomic rename, and outside observers can
see an interrupted publication before recovery. Precise durability, recovery,
and filesystem limits belong to
[statement atomicity](docs/design/statement-atomicity.md).

Recovery uses the same configured scratch root (the CLI defaults to `.temp`
under its working directory), before new runtime effects, even for an empty
script. Preserve pending recovery state until it is settled; startup does not
scan unrelated user trees. Process-crash recovery is distinct from hard
power-loss durability: the Windows implementation does not promise portable
directory-flush guarantees on arbitrary storage.
Unix publication synchronizes payloads and affected directories. Existing-file
replacement uses an atomic rename with a retained hard-linked before-image
where supported. Missing parents are staged under the highest absent ancestor,
not exposed as half-created public scaffolding. Recovery refuses a foreign
target identity rather than overwriting unrelated data.

Streams and external program effects are not crash-rollbackable finite file
statements. Ordinary files, pipes, sockets, HTTP responses, and remote byte
sources use typed source/sink adapters. Directories are structural, not byte
streams.

```text
wire => tcp://127.0.0.1:9000
$pwd::pwd/input.bin -> wire::/
```

TCP connects to the declared peer; it does not implicitly listen. Transfers are
asynchronous and backpressured. Source EOF or a closed sink ends only that flow.
Complementary FIFO halves co-start rather than waiting for each other's
completion. See [stream details](docs/design/streams.md).

## Paths and namespaces

| Form | Meaning |
| --- | --- |
| `$local` | Local filesystem; `~/` uses the local user's home |
| `$wsl` | Default WSL distribution through its file adapter |
| `$pwd::pwd`, `$pwd / "child"` | Explicit working-directory context |
| `$file::this`, `$file::parent` | Named source/module and its parent |
| `$path::bash` | Executable lookup when the relevant block executes |
| `$env::NAME/child` | Environment-derived path |
| `server => ssh://alice@host:2222` | SSH endpoint |
| `public => https://example.com/` | Read-only HTTP(S) source |
| `wire => tcp://127.0.0.1:9000` | Active TCP endpoint |

Namespace aliases follow current context; path values captured with `:=` keep
their declaration-time value. `$file` needs a named source/module; stdin and
direct REPL lines do not substitute cwd. Context substitution is not filesystem
canonicalization or existence proof. Guest `~` means the endpoint user's home.
`MSYNC_SSH_CONFIG` selects a separate client config via `-F`, without editing
existing SSH settings.

| Destination | Contract |
| --- | --- |
| `target` | Exact pathname; replace a regular file, reject an existing directory |
| `target/` | Append basename; no file clobber, recursively merge directories |
| `target/.` | Replace the directory tree, removing old children |

A missing exact directory target may be created from a directory source.
Selectors such as `configs/<.+\.toml>` match complete immediate-child basenames
using Rust `regex`; matched directories copy recursively. Selectors need a
container destination. No matches is a successful no-op. HTTP is read-only.
Symlinks/reparse points, unsupported devices, same-file aliases, and unsafe tree
overlap receive runtime checks, not heuristics.

## Build and commands

```sh
cargo build --release
msync check script.msync
msync plan script.msync
msync compile script.msync -o script.mobj
msync run script.mobj
msync run script.msync --dry-run
msync fmt script.msync --check
msync repl
```

`msync SCRIPT` means `msync run SCRIPT`; no arguments starts the REPL. Executables
are `target/release/msync` or `msync.exe` on Windows. **Run and REPL transfers
write real data unless `--dry-run` is selected.**

| Command | Contract |
| --- | --- |
| `check FILE` | Parse, type-check, expand, validate static IR |
| `plan FILE` | Print IR, ordered blocks, graph, and runtime obligations |
| `compile FILE` | Static IR preview; no endpoint probes |
| `compile FILE -o OBJECT` | Export typed IR for later interpretation |
| `run FILE` | Interpret source or object one block at a time |
| `run FILE --dry-run` | Static preview without helpers/runtime |
| `fmt FILE` | Format the script in place; never execute it |
| `doctor` | Read-only optional transport inventory; never install or launch |

Objects contain expanded executable IR and debug metadata, not file handles,
filesystem facts, or runtime proofs. Loading does not reparse debug source or
eagerly bind filesystem resources. Format markers, not extensions, identify
objects. Formatting an object is rejected without modifying it.

`fmt --check` is read-only and fails if formatting differs. `fmt --stdout` is an
explicit preview; default `fmt -` is rejected rather than treating formatting
as a transfer. Comments and values are preserved. In-place formatting refuses
symlinks and read-only scripts.

```sh
msync run script.msync --jobs 4 --max-streams 8 --buffer-size 64KiB --memory-limit 16MiB
```

`--jobs` bounds finite workers and active programs in separate slot pools;
`--max-streams` bounds active flows. Buffer
admission includes declared adapter/helper buffers. `--memory-limit` is a
payload-buffer budget, **not a process-RSS cap**: OS socket buffers, child memory,
and framework allocations are outside it. Limits do not override dependencies.

`--spool-limit BYTES` limits each program's combined stdout and stderr capture
(bytes, KiB, MiB or GiB; omitted means unlimited). Zero allows only silent
programs. Exceeding the quota reports an output error, but bounded drainers keep
reading/discarding until the child exits itself; it is not a process kill or a
process-memory limit. `/limits` reports this separate per-program disk quota.

## REPL

| Command | Action |
| --- | --- |
| `/help`, `/?` | Colored command help |
| `/pwd`, `/cd PATH` | Inspect/change cwd context |
| `/bindings`, `/reset` | Inspect/clear bindings |
| `/load FILE` | Compile/execute source or object |
| `/check FILE`, `/plan FILE` | Static inspection |
| `/fmt FILE` | Format source in place |
| `/dry-run [on\|off]`, `/limits` | Preview mode/resource settings |
| `/syntax [0.5]` | Inspect/confirm the supported grammar |
| `/jobs`, `/wait`, `/stop` | Inspect, wait, or gracefully settle owned work |
| `/kill` | Explicitly force-terminate programs; external effects may be partial |
| `/clear` | Clear viewport, not bindings/history |
| `/bash [COMMAND]`, `/pwsh [COMMAND]` | Explicit shell entry/one-shot command |
| `/quit`, `/exit` | Graceful shutdown |

Bindings/macros persist. Finish an indented macro with a blank line; Ctrl-C
discards an unfinished edit. Syntax/type failure leaves the old environment
intact. Successful compilation commits lexical bindings before IO; runtime
failure does not erase them. Shell commands do not use DSL parsing or change
the parent's cwd. Context-changing commands require quiescence.

Interactive terminals provide highlighting, completion, and in-memory history;
leading-space inputs are not recorded. Piped input has no prompts. `--color never`
disables styling, not editing. Auto color respects pipes, `TERM=dumb`, and
`NO_COLOR`.

## Diagnostics and verification

Compiler errors identify offending tokens and useful repairs. Runtime errors
name the operation, evaluated path, OS cause, and recovery action. Provenance
explains which operation requested IO without pretending a missing file is
invalid syntax. Unlocated errors never fabricate a caret on the first macro.
See [diagnostic phases](docs/design/runtime-diagnostics-v4.md).

Slow terminals show genuine phase, endpoint, and elapsed time, not an invented
percentage. Machine payloads and `--trace -` stay undecorated. Telemetry is
opt-in, local-only JSONL:

```sh
msync run script.msync --trace .temp/run.jsonl
```

Create the trace parent first. Paths, phases, causes, IDs, and durations remain
observable; payloads and unrelated credentials are not automatically captured.
See [telemetry](docs/design/telemetry.md).

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --all-targets
cargo test --test ssh -- --ignored --nocapture --test-threads=1
```

Acceptance covers compiler→object→interpreter and CLI/REPL journeys, external
file creation/deletion across barriers, bounded flows, and forced-crash recovery.
OS/backend results are separate: WSL native ext4 and Windows-backed DrvFS are
not interchangeable performance environments. See
[measurements](docs/design/performance-v4.md) and [validation](docs/design/validation.md).

License: GPL-3.0 (see LICENSE).
