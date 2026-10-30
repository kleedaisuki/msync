# Phase-aware diagnostics and optional transport inventory

This note describes the v0.5 barrier-based runtime. The filename is retained for
existing links. Old resource-view provider installation advice is not a current
runtime prerequisite.

## Classify failures before rendering source

A source map answers where an operation came from. It does **not** classify a
failure as compilation. Static analysis may succeed even when the input will
only exist after a preceding program. The current block's evaluation, actual
IO, and cleanup can fail independently of valid syntax.

| Phase | Example | Useful information |
| --- | --- | --- |
| Compile | Unknown namespace or invalid argument type | Exact token, related declaration, concrete syntax/type repair |
| Load | Invalid object schema or unsupported feature | Artifact path, schema/feature cause, rebuild guidance |
| Evaluate | Required executable/path cannot be resolved for this block | Action, evaluated context, requesting operation, environment repair |
| Execute | Opening a source or publishing a target fails | Source/sink role, evaluated path, native OS error, recovery state |
| Cleanup | Removing a settled private stage fails | Owned retained paths, actual cause, whether publication succeeded |

There is no separate eager dynamic-filesystem-link stage. Pure symbol/context
substitution does not discover the resources of future blocks. Missing files
remain runtime observations; they are not rejected while compiling a script
whose preceding program can create them.

An IO error should explain the attempted action, affected path/backend, relevant
cwd, underlying OS cause, and a useful next action. Preserve typed native causes
and their source chain; do not classify by searching English message text for
words such as `permission` or a provider's name. A cleanup warning must not
claim a known successful publication was rolled back.

## Source location roles

| Role | Meaning | Presentation |
| --- | --- | --- |
| Primary | An operation/operand has a genuine failing source site | Exact token plus the correct runtime phase |
| Context | Valid code requested an unavailable resource/action | Explain the environment failure and cite the requesting operation without calling its token invalid |
| None | Setup/recovery failure has no meaningful source site | Structured issue with action/cause/help; no synthetic source caret |

Never turn `Span::default()` into an underline at the first macro definition.
The actual expanded program launch or source/destination operand owns its
provenance. A call-site primary label may have a secondary macro-definition
label; neither should erase the native cause. File paths and terminal controls
are escaped for display, not removed from useful observability.

A failed submission is not proof that no work remains. If a later admission
fails after an asynchronous child starts, register its retained `WorkId` and
shared source provenance before mapping the error or finishing a presentation
observer. The original failure remains primary, but `/jobs` and later joins
must still identify that child. Source lifetime follows physical ownership, not
the success or failure of the command that admitted it.

Logical REPL jobs are not physical `WorkId`s. A later stdin/stdout/stderr or
completion flow belongs to the existing job that owns its native program; it
does not create a second job merely because it arrived in another submission.
Each admitted work item retains its own `ExecutionUnit` and program associations,
so joining a job cannot overwrite its launch source with a later input source.
Completed work releases that source independently. A flow connecting two jobs
merges their connected component under the oldest job ID and reports which ID
joined it. Pure physical flows without native-program associations remain
separate jobs across submissions, even when they use the same endpoint.

Attach newly admitted work before reconciling completed members, so a program
that finishes while a related flow is being admitted cannot lose its logical
job identity in between. HIR instruction IDs are compilation-local; when an
error has no owned origin, source fallback requires an unambiguous admitted
instruction/source pair rather than selecting the first matching numeric ID.

Statement startup recovery uses the same configured scratch root before new
runtime effects. A foreign target identity, active owner lock, or damaged
recovery record must identify the exact retained coordinator/control paths and
cause; it must not claim success, overwrite unrelated data, or recommend deleting
unsettled state blindly. Recoverable multi-path atomicity is distinct from
instantaneous outside visibility and portable hard-power-loss durability.

The project retains its named, multi-source, Unicode-aware renderer. Tabs, CJK
widths, combining characters, and decoded string escapes need correct column
mapping. Color policy applies consistently to command help, REPL help, source
labels, and status. `--color always`/`never` overrides auto detection; pipes,
`TERM=dumb`, and `NO_COLOR` govern auto mode.

This separation follows Rust's standard
[`Error::source` and downcasting interfaces](https://doc.rust-lang.org/std/error/trait.Error.html).
The mature [`miette::Diagnostic` contract](https://docs.rs/miette/latest/miette/trait.Diagnostic.html)
also treats labels/source as metadata rather than making them mandatory for
every environment error. Replacing the renderer or adding an error framework
does not substitute for correct phase, ownership, and location data.

## No hidden provider bootstrap

Static compilation, formatting, ordinary file assignments, and native external
programs do not require Sandboxie, bubblewrap, or a host driver. The transaction
resource-view system is no longer the execution model. A user is not told to
install a driver merely to copy four files or start a shell command. An old
`MSYNC_SANDBOXIE_HOME` environment value is not an execution prerequisite.

Only requested endpoint features require their adapter: WSL file access needs
WSL and a suitable distribution; SSH needs native clients/authentication; HTTP
needs its client. None is eagerly launched to compile a script or because an
unused macro body mentions an endpoint. An actual program launch resolves its
executable in the current block; an actual transfer evaluates its current
source and target.

`msync doctor` is a read-only inventory, not an end-to-end readiness certificate:

* Built-in compilation, formatting, ordinary publication, and async process
  execution do not trigger an optional isolation-provider probe.
* Locating `wsl`, `curl`, `ssh`, or `scp` does not prove a distribution, server,
  credentials, protocol version, or endpoint is usable.
* Clients are not launched, a distribution is not started, no driver is
  installed, and host/SSH configuration is unchanged.
* Missing optional clients are reported as inventory data rather than compiler
  errors or a reason to disable unrelated features.

On Windows, installation/executable candidates on UNC or mapped-remote drives
may be left uninspected to avoid surprising network work. An uninspected path
is not evidence of absence. The actual requested runtime action remains the
authoritative test.

## Cancellation, progress, and machine payloads

Normal cancellation stops new admissions and closes/cancels flows. Already
started external children remain owned and are joined through their own exit;
it does not promise to undo external effects or survive forced OS termination.
Distinguish a typed cancellation result from a later cleanup/IO failure; do not
mask lost recovery state as ordinary cancellation.

An explicit REPL `/kill` requests destructive program termination, unlike
`/stop`. Its result must not imply that external effects were rolled back or
completed. It is available while the REPL accepts input with background jobs;
foreground graceful drainage may still require an external OS action to force
termination. A repeated Ctrl-C is not an implicit destructive command.
Stopping a local WSL/SSH transport is not proof that its guest/server-side
descendants stopped. Destructive-stop diagnostics must retain that limitation.

Terminal progress reports actual phase, endpoint, and elapsed time, with no
invented percentage. Fast commands need not create a transient observer. Piped
stderr and `--trace -` disable human progress rendering. Formatting `--stdout`,
object `--output -`, and telemetry JSONL are exact payload surfaces, not styled
status. Human diagnostics can still be written to stderr on error; this is not
a promise that every failure produces a JSON-only stderr stream.

The progress reporter is joined before completion status and the next prompt.
Windows ConPTY emits a screen difference, not a verbatim copy of the application's
write calls: an owned native probe observed `\rjob 1: completed`, then EL0, then
the newline after progress. Overwriting column zero and erasing the old line's
tail is equivalent to clearing the whole line first. The terminal acceptance
test therefore requires an erase within that same completion row, exact visible
completion text, and no progress redraw after the new prompt; it does not require
one particular ordering of equivalent ANSI write sequences.

## Acceptance evidence

Tests must prove both classification and presentation through the CLI:

1. A syntax/type error highlights the exact offending Unicode/escaped token.
2. A missing file after valid compilation is an execution/evaluation error on
   the actual operand, not on line 1 of its macro definition.
3. A launch lookup failure cites the real call site and native cause, with no
   obsolete provider-install instruction.
4. An unused program macro does not start a process or initialize an optional
   adapter during check/plan/compile.
5. Context-free setup/recovery errors have no fabricated source location.
6. A cleanup error preserves successful publication status and retained paths.
7. Colored help remains colored when forced, while payload outputs remain exact.
8. Doctor never starts WSL/SSH/curl, installs anything, or changes configuration.

These are required journeys, not a statement that an unrun test has passed.

### Generation-8 Windows CLI gate

The frozen CLI image
`5E13804B734422D2E59201FD6C09ED7196D72727EA31E587232888FBED09B202`
passed the following direct executable suites, serially with
`--test-threads=1 --nocapture`; executable and CLI hashes remained unchanged.

| Suite | Passed | Intentionally ignored |
| --- | ---: | ---: |
| `repl_terminal` | 9 | 1 optional installed-WSL Bash journey |
| `runtime_task_telemetry_v4` | 3 | 0 |
| `repl_jobs` | 16 | 0 |
| `barrier_terminal_v5` | 2 | 1 child fixture invoked by the parent journey |

These 30 passing tests include actual ConPTY editing, progress row replacement,
Ctrl-C closing an open physical transfer without replacing its previous target,
natural completion of a running external child after Ctrl-C, retained child
output, resumed REPL use, typed connection failure with exactly one task error
event, and cancellation without a spurious task error. Test cleanup permits only
verified persistent recovery-control metadata, not retained payload or journals.
Evidence is in `.temp/barrier-cli-<suite>-gen8.log` and the hash/result manifest
`.temp/barrier-cli-results-gen8.json`. This Windows gate does not replace native
Linux, full-source CI, performance, or installed-artifact verification.

### Generation-9 logical-job gate

The Windows CLI image
`3055BF33F2378FB2A2BEACB62E9A13A7F4D47D840737E96610F169CF72431AF7`
passed 64 direct tests: binary unit tests 26, REPL jobs 16, real terminal tests
nine, task telemetry three, and structured CLI ten. One optional installed-WSL
Bash terminal journey remained ignored. Start/end image hashes were unchanged;
the serial runner exited successfully. Logs and image/result hashes are retained
in `.temp/barrier-cli-<suite>-gen9.log` and
`.temp/barrier-cli-results-gen9.json`.

The four new pure job tests exercise later input attachment with its own source,
per-work source release, oldest-ID program-bridge merging, independent physical
flows, ambiguous compilation-local instruction IDs, and exclusion of unadmitted
future port uses from grouping. These Windows tests do not execute the original
Unix-only `cat` input-failure reproduction; that journey remains a separate
native Linux gate rather than being inferred from the registration model.
