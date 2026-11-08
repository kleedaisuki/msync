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

## Directional ordering and process ownership

Unmarked external calls launch and wait. Use `release` when a Process must stay
live while subsequent work starts, then join the selected Process explicitly:

```msync
let process = release ./tool("argument");
acquire process;
```

| Form | Ordering and lifetime |
| --- | --- |
| `release operation` | Before fence; a launched Process remains live |
| `acquire operation` | Evaluate, then apply its after fence or Process join |
| `acqrel operation` | Apply both directional requirements |
| `release;` | Before fence only; later unmarked calls still launch and wait |
| `acquire;` | Join pending work owned by the current invocation |
| `acquire process;` | Join that Process, not unrelated processes |

A before fence keeps earlier work from moving after it; an after fence keeps
later work from moving before it. Neither is an unconditional global drain.
Returning from a function joins outstanding programs owned by that invocation.
A released server can therefore remain live while a later released client
starts, instead of waiting for the server's output to reach EOF first.
