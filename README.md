# msync

A typed file-synchronization DSL in Rust: a pure optimizing compiler, a
block-at-a-time interpreter, bounded asynchronous execution, source-mapped
errors, a script formatter, a persistent REPL, and native editor/LSP tooling.

> The default source grammar is **0.6**; **0.5** remains a compatible alias.
> See the [0.6 release notes](docs/releases/0.6.0.md),
> [language additions](docs/design/language-v6.md), and
> [integration contract](docs/design/integration-v0.6.md).

An optional `# msync: 0.6` header or `--syntax 0.6` selects the current grammar;
existing 0.5 headers and flags remain accepted. Syntax 0.3 and transactional 0.4 are
rejected with migration guidance, not accepted with silently changed semantics.
To migrate an old quoted filename, use an explicit joined or qualified path,
such as `$pwd::pwd / "file name"`; bare `"file name"` is literal data. All scripts
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
user configuration, scripts, data and unrelated helpers. Application settings
use TOML as described below; SSH profiles remain in OpenSSH's configuration.
Rust and the Visual C++ Redistributable are not required to use
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
for silent fallback. Configure `execution.state_dir` or use `MSYNC_STATE_DIR`
when a different state location is needed.

## Configure and inspect settings

`msync config` prints effective settings as reusable TOML, with a source comment
for each value. Inspection does not create configured state or telemetry files.

```sh
msync config
msync config --config /path/to/msync.toml
msync run script.msync --config /path/to/msync.toml --jobs 8
msync run script.msync --no-config
```

Automatic discovery checks `$XDG_CONFIG_HOME/msync/config.toml` on Unix (or
`$HOME/.config/msync/config.toml` when XDG is unset/empty), and
`%APPDATA%/msync/config.toml` on Windows. If that file is absent, it checks
`~/.msync/config.toml`. These are alternatives, not merged layers; ordinary
cwd-local configuration is never auto-loaded. `--config FILE` selects a required
file, while `--no-config` disables discovery; they cannot be combined.

Precedence is **defaults < selected config < existing compatibility environment
< explicit CLI flags**. In particular, `MSYNC_STATE_DIR` overrides configured
state. Paths in TOML resolve relative to that file's directory, not a later
script/REPL cwd. Invalid/unknown settings fail before runtime effects with the
config filename, key and source location. `--help` and `--version` bypass config
and telemetry initialization, so repair guidance remains accessible.

```toml
[execution]
jobs = 4
buffer_size = "64KiB"
memory_limit = "64MiB"

[progress]
delay_ms = 200
interval_ms = 250

[telemetry]
enabled = false
backend = "sqlite"
# path = "logs/telemetry.sqlite"
```

See the [configuration reference](docs/design/configuration-v6.md) for all keys,
limits, provenance and exporter policy. Telemetry remains opt-in; configured
custom output parents must already exist.

## Example and values

```text
macro copy_codex_config(codex, des):
    codex/config.toml -> des/config.toml
    codex/agents -> des/agents/.

codex := ~/.codex
copy_codex_config($local::codex, $pwd::pwd/local)
copy_codex_config($wsl::codex, $pwd::pwd/wsl)
```

`=` declares or updates a boolean or string; `:=` binds a typed value, including
paths and ranges; `=>` binds an endpoint, namespace, or program.
`/` joins paths, and `::` selects an item from a namespace. A quoted value is
UTF-8 data, not a filename inferred from its position or current existence:

```text
"hello, 可莉" -> message.txt
$pwd::pwd / "input file.txt" -> $pwd::pwd / "copied file.txt"
```

Path components may contain internal spaces but not leading/trailing whitespace.
Literal content is preserved byte-for-byte. Macros expand typed values in an
isolated lexical environment. `global name` imports a binding from the macro's
definition-time environment; caller locals are not implicitly captured.

`$pwd` is a space; `$pwd::pwd` is its working-directory **item**. For example,
`here => $pwd` aliases the space (use `here::pwd`), whereas
`cwd := $pwd::pwd` captures the cwd path value. Naming a space with `=>` does not
select its item. `/` has no space overload: select the item first. Space-related
home access uses `~`, as in `remote::~`, rather than `/`.

`/` evaluates both item operands before composing paths: `:=` bindings are
expanded even on the right. A rooted right item cannot replace a path base.
String literals have explicit path-fragment overloads inside `/`; a bare quoted
transfer source still means literal data. See the [complete operator and space/item
definitions](docs/design/language-v4.md#-operator-evaluated-item-composition).

## Compile-time functions and source composition (0.5.2)

```text
symbol home(space):
    return space::~

config := home($local) / ".codex"
include "shared.msync"
execute "tasks/<.+\\.msync>"
```

Symbols inline statically with independent lexical environments, generic value/
space arguments, nested calls, explicit definition-time `global`, and a mandatory
return. Their bodies are pure; they do not emit transfer or program work. `include` shares
lexical bindings; `execute` isolates each source's bindings. Both inline before
runtime, sort selector matches lexicographically, and add no scheduling blocks.
Source paths are currently compiler-local files; ordinary data-path and cwd
semantics do not change when a child file is loaded.

Use **release / acquire / acqrel** for scheduling. Old async/await spellings remain
compatibility aliases; canonical formatting uses the new names. Full rules and
bounds are in the [language reference](docs/design/language-v4.md).

## Booleans, conditions and file queries (0.6)

```text
boolean any(a, b, c):
    return a or b or c

label = "klee"
ready = label >= "kle" and not false
"payload" -> scratch.txt
present = exists scratch.txt
delete "scratch.txt"
if any(false, ready, false) and present:
    label -> selected.txt
else if exists "fallback.txt":
    fallback.txt -> selected.txt
else:
    "no input" -> selected.txt
```

Booleans are exactly `true` or `false`. Operators are `not`, `and`, `or`, `==`
and `!=`, with parentheses for grouping; `and` and `or` short-circuit. Strings
support case-sensitive lexicographic `>`, `<`, `>=`, `<=`, `==` and `!=`.
Conditions require a boolean, never implicit string/path truthiness.
Pure expressions and function calls fold at compilation when possible.
Assigning an existing name with `=` preserves its boolean or string type.
Updates inside `if`, `for` or `loop` remain visible to subsequent code in the
same invocation; a new name declared inside a branch or loop stays local.
`exists` always observes the filesystem at execution, after preceding work;
an assigned result is a snapshot, not a query repeated on every reference.
`boolean` functions require a boolean `return` and can use comparisons and
`exists`. Both `exists` and `delete` accept a path or a filename string;
`delete` removes a file, not a directory tree. It is distinct from `drop` below.
Existence follows symbolic-link targets: dangling links are false, directories
are true, and permission or symlink-loop errors remain errors. Deletion unlinks
a symbolic link without deleting its target. Filesystem queries support local,
environment, WSL and SSH paths; PATH lookup is query-only. HTTP/TCP are not
filesystem-query or deletion endpoints.

### Lazy ranges and loops

```text
boolean present(path):
    return exists path

symbol message(path):
    return "selected"

files := inputs/<.+\.txt>
for text in files | views::filter(present) | views::transform(message) | views::take(1):
    text -> result.txt
if files | ranges::any_of(present):
    "nonempty" -> status.txt

chosen = loop:
    loop:
        break
    break "done"
chosen -> completion.txt
```

Regex selectors construct lazy ranges of paths. Views compose with `|`;
predicates are boolean functions and projections are symbols. Elements may
be paths or strings, not booleans: boolean consumers such as `any_of` return
a scalar boolean instead. Enumeration starts only at consumption; filters and
projections execute only for demanded elements. Directory-entry ordering remains
deterministic. `loop` and `for` nest by indentation; `break` exits the nearest
loop. A value-producing `loop` requires compatible `break` values of string,
path, boolean, or space type. See the [range contract](docs/design/ranges-v6.md)
for the supported adaptors and domain limits.
Element consumers such as `front`/`back` require a nonempty range; `find` and
`find_if` report an error when no element matches rather than inventing a path.

Existing boolean/string bindings can carry state across iterations:

```text
done = false
message = "working"
loop:
    if done:
        break
    message = "complete"
    done = true
message -> result.txt
```

### Nested definitions and binding lifetime

```text
Codex := ~/.codex
macro codex():
    global Codex
    label = "copied"
    macro copy():
        global Codex, label
        boolean enabled():
            boolean yes():
                return true
            return yes()
        symbol identity(value):
            symbol inner(item):
                return item
            return inner(value)
        if enabled():
            acquire Codex/config.toml -> s.txt
            identity(label) -> status.txt
    copy()
codex()
drop Codex, codex
```

Macros may declare local macros, symbols and booleans. Symbols may declare local
symbols; booleans may declare local booleans. Each invocation has an independent
lexical environment. `global` imports an earlier binding from definition-time
ancestry, including parent macros, not unrelated caller locals. Local definitions
do not leak outward. Function/macro parameters and imported globals are
invocation-local values, not references for mutating caller bindings.
`drop a, b` removes named bindings/templates; it does not
delete files or promise immediate resource reclamation.

## Reusable IR commands

`emit-ir`, `bind-ir`, `plan-ir` and `run-ir` remain available. New exports contain
expanded typed object IR, including literals, programs, barriers and typed control flow;
they never depend on re-reading the original script. `emit-ir FILE -o portable.json`
keeps context roots symbolic. `bind-ir portable.json -o bound.json` or
`emit-ir FILE --ir-stage contextual` captures local path bindings. Runtime file
observations and optimization proofs are always rebuilt. Use `--source-file FILE`
when binding a portable module that accesses `$file`.

The REPL retains `/export-ir FILE`, `/export-relocatable FILE`, and `/load-ir FILE`.
Version-1 artifacts exported by 0.4 retain their original explicit execution
semantics; they are not reinterpreted as current source. `compile FILE -o module.mobj`
also exposes the language branch's native relocatable object format.
New control-flow semantics carry an explicit object feature; existing 0.5 objects
remain supported. See the [IR contract](docs/design/ir.md) and
[0.6 integration](docs/design/integration-v0.6.md).

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
`acqrel Program => $path::bash("-c", "ls")`: launch through the asynchronous
runtime and wait for that statement's completion. Explicit `release` permits
continued interpretation after launch:

```text
release Program => $path::bash("-c", "ls")
Program::stdout -> stdout.txt
acquire Program
```

| Form | Boundary |
| --- | --- |
| `release STATEMENT` | Earlier work cannot move after its start boundary |
| `acquire STATEMENT` | Later work cannot move before its completion boundary |
| `acqrel STATEMENT` | Both boundaries |
| `acquire Program` | Wait for that program and connected port work |
| `acquire` | Wait for outstanding work |
| `release` | Standalone start-side ordering boundary |

These are directional barriers, not syntax that must be paired. The compiler
inserts a wait at macro exit for asynchronous programs started in that macro and
not already awaited. A macro does not silently leak an unfinished local child.

`release` still allows later independent work to move into an earlier block;
`acquire` still allows earlier independent work to move into a later block. The
compiler normalizes these one-way constraints and removes unnecessary blocks
before building dependency graphs. Real launches and joins remain observation
anchors; data dependencies and possible aliases still constrain execution.

For a program's newly created files, wait for that program before reading them.
`release` alone guarantees launch ordering, not file readiness:

```text
Generator => $path::bash("-c", "printf generated > generated.txt")
generated.txt -> copied.txt
```

The implicit wait finishes `Generator` first; only then does the interpreter
inspect `generated.txt`. With an explicit release launch, place `acquire Generator`
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
| `$pwd::pwd`, `$pwd::pwd / "child"` | Explicit working-directory context |
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
| `config` | Print effective TOML settings and their sources without creating outputs |
| `telemetry COMMAND DB` | Read/query/export an existing local telemetry database |
| `--language-server` | Serve the editor protocol over stdio, not an interactive REPL |
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
| `/syntax [0.6\|0.5]` | Inspect/select the current grammar or compatibility alias |
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

Background completion is event-driven: a finished flow or fully drained child
can publish its result and display its completion notice while input remains
idle. Interactive notices use the editor's external printer to preserve an
unfinished line; progress refresh settings do not control completion polling.
See the [latency contract](docs/design/latency-v6.md).

## Editor and language-server workflow

The native VS Code extension associates `.msync` files with TextMate coloring,
indentation, comments, brackets and snippets. Its stdio LSP client adds compiler
diagnostics, completion, hover, definition lookup, document symbols, formatting
and semantic tokens. Coloring remains available if the server cannot start.

From this checkout, install the packaged extension in VS Code 1.91 or newer:

```sh
code --install-extension dist/editor/msync-language-0.6.0.vsix
```

Alternatively use **Extensions: Install from VSIX** with that file. Install
`msync` on PATH, or set the user/machine setting to a trusted executable:

```json
{
  "msync.serverPath": "/absolute/path/to/msync"
}
```

Open a `.msync` file; the extension launches `msync --language-server` directly.
Other LSP-capable editors can launch the same command over stdio and register
the `msync` filetype. Do not use the normal REPL command as the server. LSP stdout
contains only framed protocol messages; diagnostics never execute DSL transfers,
deletion, existence checks or programs. Compiler source expansion may read
referenced `include`/`execute` files, but navigation/completion are currently
document-local, not a project-wide include index.

The production extension also passed an automated real VS Code 1.144.0/Electron
host journey under Xvfb: activation, diagnostic repair, formatting, completion
and Unicode semantic tokens, with no DSL file effects. This is actual editor-host
integration, not human visual inspection or a claim about unexecuted platforms.
Build/development instructions and dependency audit notes are in
[the extension README](editors/vscode/README.md); exact bundle/VSIX identities and
host evidence are in [editor tooling](docs/design/editor-tooling-v6.md), with
capability boundaries in the [toolchain reference](docs/design/toolchain-v6.md).

## Diagnostics and verification

Compiler errors identify offending tokens and useful repairs. Runtime errors
name the operation, evaluated path, OS cause, and recovery action. Provenance
explains which operation requested IO without pretending a missing file is
invalid syntax. Unlocated errors never fabricate a caret on the first macro.
See [diagnostic phases](docs/design/runtime-diagnostics-v4.md).

Slow terminals show genuine phase, endpoint, and elapsed time, not an invented
percentage. Machine payloads and `--trace -` stay undecorated. Telemetry is
opt-in and local, with SQLite recording or the existing JSONL exporter:

```sh
mkdir -p .temp
msync run script.msync --telemetry-db .temp/run.sqlite
msync telemetry sessions .temp/run.sqlite --limit 10
msync telemetry summary .temp/run.sqlite
msync telemetry spans .temp/run.sqlite --limit 20
msync telemetry events .temp/run.sqlite --limit 20
msync telemetry export .temp/run.sqlite > .temp/run.jsonl

# Choose JSONL directly instead of SQLite:
msync run script.msync --trace .temp/run.jsonl
```

SQLite stores normalized sessions, spans/events, attributes, metrics and native
error causes, with causal trace/span/parent identities through compilation,
preparation, scheduling, instructions and I/O. `sessions` exposes completed,
failed and unfinished recordings. Query commands open the database read-only;
they do not initialize a missing database or enable a second recording session.
Add `--trace-id ID` to `summary`, `spans`, `events` or `export` to select a session;
`events` also accepts `--span-id ID`. Export preserves the JSONL record contract.
Phase-duration sums can overlap and are not elapsed wall time.

Create custom output parents first. `--trace` and `--telemetry-db` select different
exporters and cannot be combined; `--no-telemetry` disables configured recording.
Paths, phases, causes, IDs and durations remain observable; payloads and unrelated
credentials are not automatically captured. Bounded queues apply backpressure
rather than silently dropping causal records; flush/storage failures are visible.
SQLite WAL is local-host storage, not a shared network-filesystem logging service.
See [telemetry](docs/design/telemetry.md),
[SQLite storage](docs/design/telemetry-sqlite.md), and
[telemetry configuration](docs/design/configuration-v6.md#sqlite-telemetry-policy).

Quality verification is local-first. Select affected contracts rather than
automatically repeating the complete suite:

```sh
./scripts/check-local.sh --test barrier_crash_v5 --test empty_recovery_v6 --test latency_v6
./scripts/check-local.sh --editor
./scripts/check-local.sh --packaging
```

The local entry point runs formatting and strict Clippy for selected Rust work;
editor/packaging-only selections do not invoke Cargo. No arguments select Rust
lib/bin unit tests. `--full` explicitly selects all Rust targets and doctests.
Windows developers can use Git Bash; the underlying Cargo/npm commands are
native. Optional authenticated SSH journeys remain explicit:
`cargo test --locked --test ssh -- --ignored --nocapture --test-threads=1`.

GitHub Actions supplies cross-platform behavior and native package/editor-host
acceptance plus publishing, not a second lint/format quality gate or an external
CI-status prerequisite for publication. Required assets, matching versions,
checksums and published-tag immutability remain release invariants.

Acceptance covers compiler→object→interpreter and CLI/REPL journeys, external
file creation/deletion across barriers, bounded flows, and forced-crash recovery.
OS/backend results are separate: WSL native ext4 and Windows-backed DrvFS are
not interchangeable performance environments. See
[measurements](docs/design/performance-v4.md) and [validation](docs/design/validation.md).

### Performance work without changing language behavior

The accepted optimization work preserves the frozen 0.6 language/object baseline.
It reduces repeated compiler specialization and lexical snapshot work, indexes
logical graph conflicts and preparation history, borrows recursive object
analysis, and removes periodic REPL completion polling. I/O fast paths remain
subject to endpoint, cancellation and publication contracts. Detailed mechanisms,
workloads and measurement boundaries live in the
[compiler](docs/design/performance-v6-compiler.md),
[graph](docs/design/performance-v6-graph.md),
[preparation](docs/design/performance-v6-preparation.md),
[object](docs/design/performance-v6-object.md),
[I/O](docs/design/io-linux-v3.md), and
[latency](docs/design/latency-v6.md) notes.

Representative accepted Linux/ext4 results, with exact images and procedures in
the linked notes:

| Workload | Before → after median |
| --- | ---: |
| Complete CLI plan, 100 independent statements | 1541.247 → 423.755 ms |
| Preparation component, 500 independent statements | 3697.343 → 330.495 ms |
| Interpreter component, 2,000 pure-control iterations | 7.647 → 3.502 ms |
| Actual CLI copy, 64KiB to a missing destination | 41.016 → 38.220 ms |

**Not every command is faster.** Tiny version/help/check/plan/REPL-help commands
retain an approved approximately 0.09–0.41ms fixed increase for the integrated
configuration/tooling/observability capabilities. Only about 0.11ms was directly
attributed to configuration discovery in a separate phase profile; the remaining
combined setup cost was not causally isolated. Persistent-copy samples remain
mixed: a negative initial point triggered 202 crossed-workspace pairs rather
than an unsupported claim of either universal regression or zero overhead.

SQLite recording has a real opt-in cost: one actual 64KiB-copy cohort measured
38.039ms off versus 48.120ms warm SQLite; cold SQLite measured 53.103ms against
its own 38.606ms off control. Full tracing is not promised free. The
[latency and exporter-cost record](docs/design/latency-v6.md) retains small-command
results, distributions, independent idle notices, retention checks and mixed
interval findings. The [completed requirement map](docs/design/optimization-roadmap.md#current-v06-completion-map-functional-and-measured-acceptance-closed)
and [cross-domain functional acceptance](docs/design/performance-acceptance-v6.md)
connect these measurements to actual configuration, SQLite, CLI/REPL and editor
journeys. Linux results do not certify native Windows/WSL or remote CI execution.

License: GPL-3.0 (see LICENSE).
