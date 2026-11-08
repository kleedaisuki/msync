# A complete editing and execution workflow

```sh
msync check job.msync
msync fmt job.msync --stdout
msync fmt job.msync
msync fmt job.msync --check
msync plan job.msync
msync run --dry-run job.msync
msync run job.msync
```

`check` compiles without executing filesystem or process effects. `plan` prints
typed functions and basic blocks. `--dry-run` previews selected operations; it
does not prove remote access or promise success on later filesystem state.
`fmt` uses the same grammar as the compiler with an 80-column layout target.
Long indivisible literals and deeply nested structures are not truncated.

Start `msync repl` for persistent bindings and functions. `/help` lists editing
commands, `/syntax` shows a syntax reference, and `/quit` leaves the session.
Tab completes bindings and local paths; Shift+Enter inserts a newline; Ctrl+C
cancels input or current work without replaying earlier completed effects.

```msync
fn copy(source: Path, destination: Path) { source -> destination };
copy(./input.txt, ./backup.txt);
```

`msync config` prints effective configuration and origins. `--no-config`
disables discovery for a reproducible isolated invocation. `--color never`
and `NO_COLOR` produce plain terminal guidance; piped output is plain by
default. `msync skill` is available offline without configuration or telemetry.

For source-independent execution and contextual binding, expand
`msync skill workflow artifacts`.
