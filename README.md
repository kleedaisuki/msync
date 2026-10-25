# msync

A small, typed file-copy language implemented in Rust: parser, compiler, execution-plan interpreter, comment-preserving formatter, and persistent REPL.

```text
remote => $wsl
.codex := ~/.codex

.codex/config.toml -> remote::.codex/config.toml
.codex/agents/<.+\.toml> -> remote::.codex/agents/
```

The initial language contract is the supplied [`spread.msync`](docs/dsl/spread.msync). The compiler lowers source into a resolved, typed intermediate representation (IR), not native machine code. The interpreter executes that plan; checking and compilation never copy files.

## Build and run

```sh
cargo build --release
cargo run -- check docs/dsl/spread.msync
cargo run -- compile docs/dsl/spread.msync
cargo run -- run docs/dsl/spread.msync --dry-run
cargo run -- fmt docs/dsl/spread.msync
cargo run -- repl --dry-run
```

The release executable is `target/release/msync` (`msync.exe` on Windows). `msync SCRIPT` is shorthand for `msync run SCRIPT`; no arguments starts the REPL. Pass `-` instead of a filename to read UTF-8 source from stdin. `--help` lists the CLI.

**`run` and ordinary REPL statements perform real writes unless `--dry-run` is enabled.** The example references personal configuration directories: inspect its plan before running it.

## Language

Each nonempty line has one statement. `#` starts a comment at a whitespace boundary, outside quotes and selectors. Adjacent URL fragments remain part of the operand. Whitespace around operators is optional. Names and paths may contain Unicode.

| Form | Meaning |
| --- | --- |
| `remote => $wsl` | Bind an endpoint name |
| `server => ssh://alice@host:2222` | Bind an SSH endpoint |
| `public => https://example.com/` | Bind a read-only HTTP(S) endpoint |
| `.config := ~/.config` | Bind a path alias |
| `source -> target` | Copy to this exact path; overwrite existing files, merge directories |
| `source -> directory/` | Append source basename; reject existing files, merge directories without overwriting their files |
| `directory/<.+\.toml> -> output/` | Select immediate children by a full-basename regex match |

Selectors use Rust's linear-time `regex` syntax; backreferences and lookaround are not supported. A selector is the final source component, and its destination must end in `/`. Matching subdirectories are copied recursively, but selector enumeration itself is not recursive. No matches is a successful no-op.

Directory paths and ordinary file paths use the same model: **the destination's trailing `/` determines the mode**, not whether the path happens to exist. Unmentioned destination children remain in place. This is ordered one-way copying, not deletion, reconciliation, a watch daemon, or a bidirectional synchronizer.

### Endpoints and aliases

An unqualified path is local. `endpoint::path` chooses a named or built-in endpoint:

| Endpoint | Behavior |
| --- | --- |
| `$local` | Native local filesystem; leading `~/` uses HOME or USERPROFILE |
| `$wsl` | Default installed WSL distribution, via `wsl --exec` |
| `$path` | Read-only single filename lookup in PATH, e.g. `$path::gcc.exe` |
| `$env` | First component is an environment variable, e.g. `$env::LOCALAPPDATA/tools/` |
| SSH alias | Filesystem on the remote host, using `ssh` and modern `scp`/SFTP |
| HTTP(S) alias | Read-only single-file download using `curl` |

Endpoint declarations may refer to earlier endpoints. Aliases expand on a **whole first component**, not a string prefix: `.config/x` expands `.config`, but `.config-old/x` does not. Alias values are resolved when declared, so forward references and implicit recursive substitutions do not occur. Redefinition is allowed for interactive use. `a := a/child` extends an existing `a`; without an existing binding, its right side is an ordinary literal path.

An unqualified alias remains endpoint-relative: `.config := ~/.config` works with both `.config/x` and `remote::.config/x`. An explicitly qualified alias retains its endpoint; adding a conflicting endpoint is a compile error. User binding names contain letters, digits, `_`, `-`, or `.`, and cannot be `.` or `..`.

On filesystem endpoints, `~` denotes the current user's home (not `~another-user`). On HTTP, `~/` and a leading `/` are endpoint-relative prefixes; URL joining follows normal URL rules. Give an HTTP base a trailing `/` to append below a directory. HTTP credentials belong in client configuration, not endpoint declarations. All selectors and endpoint restrictions are checked before execution; environment and PATH lookup happen at execution time.

### Quoting and Windows paths

Quote an entire operand when it contains whitespace, a comment delimiter, or an operator:

```text
"source files/config.toml" -> "output files/config.toml"
"C:\\Users\\Klee\\config.toml" -> output/config.toml
```

Inside double quotes, `\\`, `\"`, `\n`, `\r`, and `\t` are escapes. Unknown escapes retain their backslash. To avoid ambiguity with `\t` and `\n`, use `/` or doubled backslashes in quoted Windows paths. Unquoted backslashes are literal. A trailing **forward slash** is the portable directory-mode marker even on Windows. The formatter preserves comments and operand values, normalizes spacing and quoting, and ends nonempty output with a newline; blank-line layout is not retained.

```sh
msync fmt script.msync             # Print canonical source
msync fmt script.msync --check     # Exit nonzero if formatting differs
msync fmt script.msync --write     # Replace the file using a temporary sibling
```

## REPL

```text
msync> /?
msync> root := ./configs
msync> /dry-run on
msync> root/app.toml -> output/app.toml
msync> /bindings
msync> /quit
```

Bindings persist between submissions. Parse or compile failure leaves the previous environment unchanged. Successful compilation commits declarations; a subsequent runtime failure does not roll back that environment or completed filesystem writes. Each input line is a submission; `/load` executes a multiline script as one compilation transaction.

| Command | Action |
| --- | --- |
| `/help`, `/?` | Command help |
| `/pwd`, `/cd PATH` | Inspect or change the working directory |
| `/bindings`, `/reset` | Inspect or clear user bindings |
| `/dry-run [on\|off]` | Inspect or change offline preview mode |
| `/load FILE` | Compile and execute using the current bindings |
| `/check FILE`, `/plan FILE` | Check or print a plan without changing bindings |
| `/fmt FILE` | Print canonical source |
| `/quit`, `/exit` | End the session |

Scripts can pipe statements and commands into `msync repl`. Prompts appear only with terminal input. A failed submission prints a diagnostic and the REPL remains usable; a cleanly terminated REPL exits successfully even if an earlier submission failed. `:help`, `:quit`, and corresponding colon commands are also accepted for convenience.

## Diagnostics and telemetry

Diagnostics include the input filename, line/column, source gutter and caret. Tabs, CJK display widths, combining characters and invalid byte-boundary spans are handled safely; terminal control characters in source/diagnostic messages are escaped. Diagnostic/prompt/status colors are automatic on terminals, disabled for pipes, `TERM=dumb`, or `NO_COLOR`; use `--color always` or `--color never` to override. ANSI output adapts to Windows console capabilities through `anstream`.

```sh
msync run script.msync --dry-run --trace .temp/preview.jsonl
msync check script.msync --trace -     # JSONL spans to stderr
```

Create the trace destination's parent directory first. Telemetry is **off by default, local-only, and never uploaded**. Every enabled session has one trace ID; read, submission, parse, compile, format, execute and copy spans share parent relationships, durations, status, and numeric metrics. No source text, paths, environment contents or error bodies are recorded. The output is versioned JSONL, follows trace/span conventions, and is not an OTLP wire exporter. Export failure is surfaced, and buffered spans are flushed before the CLI exits. See [the telemetry contract](docs/design/telemetry.md).

## Safety and platform requirements

- Local execution preflights each operation's selected trees before mutation, rejecting ordinary conflicts, source/destination identity including hard links, recursive copying into the source, symlinks, Windows reparse points, and special files. Insert-mode file creation is atomic no-clobber.
- Copy operations execute in order. A later operation or disk/network failure may leave earlier writes in place. There is **no whole-script filesystem rollback**, crash-durable transaction, or defense against an attacker concurrently replacing parent directories. A dedicated filesystem-capability implementation would be needed for the latter.
- `--dry-run` is fully offline: no filesystem writes, network requests or remote process launches. It previews resolved operations without promising that sources exist or remote permissions will permit them. `--trace` is an explicit exception that writes telemetry.
- HTTP needs `curl`; WSL needs a configured distribution; SSH needs `ssh`, modern SFTP-default `scp`, and authentication configured outside the DSL. Missing clients and failed operations produce diagnostics. No new SSH service or system configuration is installed.
- To select an independent SSH client configuration, set `MSYNC_SSH_CONFIG` to its filename. Both `ssh` and `scp` receive `-F` without changing the default behavior when the variable is absent. This also permits isolated test credentials and known-hosts files without editing existing SSH settings.
- Remote hosts need POSIX `sh` and GNU-compatible `find`, `realpath`, `cp`, `chmod`, `mktemp`. Filenames are shell-quoted. Transport scratch data is exclusively created under `.temp` in the working directory and RAII-cleaned; SSH upload staging uses a private remote `mktemp` directory.
- Before SSH downloads, every remote tree entry is checked for native filename representability. Windows rejects reserved devices, separators/ADS, invalid characters, trailing dots/spaces and conservative uppercase path collisions rather than silently changing names. Native Windows SCP cannot preserve POSIX backslashes in remote paths; such requests fail explicitly.
- Permissions are copied where supported. ACLs, ownership, timestamps, alternate streams and every platform-specific metadata attribute are not a portable preservation contract.

## Library and development

```rust
use msync::{compiler::Compiler, runtime, syntax};

let source = "configs/app.toml -> output/app.toml\n";
let ast = syntax::parse(source).expect("valid syntax");
let plan = Compiler::new().compile(&ast).expect("valid semantics");
let report = runtime::execute(
    &plan,
    &runtime::ExecutionOptions { dry_run: true },
).expect("preview");
assert_eq!(report.actions.len(), 1);
```

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test --all-targets
cargo test --test runtime -- --ignored --test-threads=1  # Explicit real WSL test on Windows
cargo test --test ssh -- --ignored --nocapture --test-threads=1  # Isolated real SSH/SFTP in WSL
cargo bench --bench pipeline
```

Tests include actual CLI/REPL sessions, local filesystem operations, localhost HTTP, telemetry graph validation, and property-based formatter/runtime checks. The explicit SSH suite requires Windows OpenSSH and an installed WSL distribution with root execution, `sshd`, and mount/PID namespace support; it starts its own temporary chrooted service, not an existing SSH server. No system service, account, or persistent SSH configuration is changed. [Architecture](docs/design/implementation.md), [syntax](docs/design/syntax.md), [runtime safety and SSH fixture](docs/design/runtime.md), [validation](docs/design/validation.md), and [performance results](docs/design/performance.md) document decisions and evidence.

License: GPL-3.0 (see the existing LICENSE file).
