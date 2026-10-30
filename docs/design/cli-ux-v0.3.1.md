# v0.3.1: readable CLI feedback and an editable REPL

## Product intent

The user moves between a synchronization script, a persistent DSL environment,
and a host shell. The v0.3.0 baseline makes those transitions unnecessarily
hard: a missing script is a raw OS sentence, empty bindings look like nothing
happened, input is an unstyled buffered line, and clearing the screen or opening
a shell requires leaving the session. This release should make the next useful
action apparent without turning the terminal into a decorative dashboard.

The complete experience is: discover a command, edit a highlighted statement,
understand its result or repair its failure, temporarily use a shell, and return
to the same working environment. Existing file/stream semantics, plan safety,
resource limits and pipe-driven automation remain intact.

## Product decisions

1. Use one visual vocabulary for human messages: result/severity first, affected
   object next, retained technical cause and a useful recovery step when needed.
   Color reinforces meaning; text must still explain it without color.
2. Highlight input while editing, including unfinished input. Lexical coloring is
   not a parse/compile operation and must not perform filesystem or network I/O.
   Red is for submitted errors, not an ordinary unfinished quote or operator.
3. Use a mature cross-platform line editor for history, cursor movement and
   terminal restoration. Keep session history in memory; automatic history-file
   persistence is not necessary for this release.
4. Add `/clear`, `/bash` and `/pwsh` as explicit environment commands. Shell
   command text is interpreted only after one of those explicit shell entries,
   never by the ordinary DSL compiler.
5. Preserve plain machine/payload surfaces: the library's plan representation,
   canonical `fmt --stdout` text and trace JSONL are not colored or decorated.
   The CLI may style human plan/status wrappers without changing those contracts.
6. Preserve useful paths, native error causes and process exit status. Escape
   terminal controls in app-owned text; do not hide diagnostic context in the
   name of privacy. A child shell owns its intentionally inherited terminal I/O.

## Audited user-facing surfaces

This is a behavior inventory, not a requirement to replace every error with a
different framework. Related messages should share a small number of templates.

| Surface / current entry point | Required product treatment |
| --- | --- |
| Global help and `/?` / `/help` | Task-grouped commands, consistent spacing, compact examples, explicit namespace items, shell/clear shortcuts, exit and input-interruption hints |
| Version | A simple version line; no banner noise in `--version` |
| Missing command input / unknown option / invalid resource value | Error headline, offending option/value, expected form, nearest useful help; requested early `--color` policy must apply |
| Script intake: run/check/plan/compile/fmt and REPL file commands | Describe the attempted action and script path; distinguish not found, denied access, directory instead of file, invalid UTF-8 and other I/O causes |
| Stdin read failure | Identify stdin rather than inventing a filename; preserve cause; do not pretend data was parsed |
| Source-context capture / current directory failure | State that the working directory could not be resolved, retain cause, suggest moving to an existing directory or supplying explicit context in the library |
| Parser/compiler diagnostics | Keep exact source spans, source identity, original escaped tokens, related labels and constructive help; no duplicated error headlines or terminal-control injection |
| Preparation/type/graph failures | Explain the offending source/destination or co-start resource requirement; keep existing exact operand provenance and useful repair advice |
| Native/HTTP/WSL/SSH execution failure | Identify operation/endpoint and phase, retain native/transport cause and progress/commit context; do not flatten to a generic failure |
| Cancellation / partial work / uncertain remote commit | Distinguish cancelled work, already committed files and unknown remote publication; never imply a whole-tree rollback |
| Formatter success/no change/check difference/write refusal | In a terminal, make formatted/already formatted/check mismatch clear with the file path; preserve canonical stdout preview and nonmutation of check/preview modes |
| CLI check success | Explain that syntax/bindings were checked, not that every source exists or every remote endpoint was contacted; retain `ok` compatibility in plain output |
| Plan / offline preview / optimization explanations | Distinguish metadata-only planning from execution; emphasize retained operations, dependencies and rewrite reasons; preserve `plan:`/`preview:` plain prefixes |
| Per-transfer actions and final summary | Readable source/destination relationship, a restrained result label, legible counts/byte units; preserve `done:` and existing counter meaning in plain output |
| REPL startup / prompt / empty input | Version/discovery information only in an interactive session; no prompts or unsolicited banners when stdin is piped |
| Successful binding / empty bindings / reset | Make terminal state changes legible. Binding output should stay reusable as DSL; an empty-state DSL comment is preferable to an ambiguous blank line |
| `/pwd`, `/cd` and command usage | Show useful cwd/path context and a targeted missing-directory/access error; `/pwd` remains a plain path line under redirection |
| `/dry-run` and `/limits` | Make preview versus live writes apparent; distinguish finite slots, background slots, per-flow buffer and aggregate payload budget, which is not RSS |
| `/jobs`, `/wait`, `/stop`, automatic reap | Stable job ID and state, expected next command, informative no-active-job state; cancellation is not a red failure banner |
| Unknown REPL command / unexpected arguments | Show the command and valid usage or a plausible spelling; reject unsupported arguments instead of silently ignoring them |
| `/clear` | Clear only the visible terminal viewport and redraw the prompt; preserve bindings, job, cwd, mode and history |
| Shell entry, shell result and shell launch failure | Identify the chosen program and cwd, restore terminal ownership correctly, show nonzero exit status or spawn cause, return to the existing REPL |
| Trace initialization/flush failure | Identify the trace target and failing phase; successful file transfers must not be described as undone merely because trace export failed |
| Output write/internal worker failure | Preserve the real cause and operation context; no false success, recursive error spam, or new panic masking |
| Trace JSONL / formatter stdout / child-shell output | Respect each payload's owner and protocol; no unsolicited labels, ANSI decoration or grammar rewriting |

The v0.3.0 README still contains an outdated telemetry sentence claiming paths
and error bodies are excluded. It must be reconciled with the actual useful-path
and cause-chain contract in `telemetry.md` while polishing user-facing docs.

## Visual and input contract

Use ordinary terminal colors with readable defaults on light and dark themes:
bold red for error labels/primary marks, restrained yellow for actionable
warnings, green for successful result labels, cyan/blue for commands and
namespaces, a distinct operator style, green quoted operands, yellow selectors,
and dim comments/help context. Do not color entire explanatory paragraphs or
use red for normal status/empty state. No information may depend on hue alone.

The editor differentiates namespace prefixes/items, binding names, operators,
quoted operands/escapes, selectors and comments. It must follow the DSL's actual
comment boundaries, quoted escapes, regex character classes and operators inside
selectors. It must not mistake a URL fragment for a comment or `>` in a regex
class for the end of a selector. Unterminated strings/selectors are normal edit
states. Rendering does not change submitted bytes, character positions or visible
width; formatting remains an explicit command.

Command-name completion and inexpensive known namespace/binding hints are useful
if supported by the selected editor. They must not introduce remote discovery,
consume a FIFO/socket, silently execute text, or turn a suggestion into submitted
input without an explicit acceptance action. In-memory up/down history and
normal cursor/backspace editing are part of the coherent interactive experience.

`--color auto` respects terminal capability, `NO_COLOR` and `TERM=dumb`;
`always`/`never` override the automatic color choice. Disabling color must not
disable editing, shell entry or other interactive functionality. Piped input
keeps the buffered/noninteractive path, with no prompts, cursor control or ghost
hints. Canonical formatter output and trace JSONL stay protocol-clean even when
human output is explicitly colored.

## REPL environment transitions

| Input / state | Observable outcome |
| --- | --- |
| Ctrl-C while editing, no active plan | Discard the edit and return to a clean usable prompt; no execution or failure banner |
| Ctrl-C with an active plan | Cancel/join the active plan as the published stream-interruption contract promises, discard the edit, then return to the prompt |
| Interactive EOF | Exit after cancelling/joining any active plan |
| Piped EOF | Preserve the existing behavior of waiting for the remaining active plan rather than cancelling it |
| `/clear` in a real terminal | Clear the visible viewport and redraw; do not reset the terminal, delete scrollback/history, clear bindings or stop a job |
| `/clear` with redirected output | No escape sequences; either a quiet no-op or a concise plain informational note, never a fabricated reset |
| `/bash` / `/pwsh` without an argument | Require a real terminal, hand over cooked terminal/stdin/stdout/stderr to that interactive shell, then resume msync after shell exit |
| `/bash COMMAND` / `/pwsh COMMAND` | Pass the raw command tail through that explicitly selected shell, wait, report abnormal status and return; command mode can work with piped stdin |
| Shell attempt while a plan is active | Reject before spawning with `/wait` or `/stop` guidance, like existing shared-state-changing commands |
| Shell command changes cwd/environment or exits | Its changes belong to the child process; msync's cwd, bindings, dry-run setting and limits remain unchanged |
| Missing shell / inaccessible executable | Name the requested shell, attempted executable/backend and native cause; suggest installing it/on-PATH setup or the supported alternative; keep the prompt usable |
| Ctrl-C during a child shell command | Interrupt the child foreground work without terminating msync; restore the editor and accept a subsequent command |

Do not apply the existing generic `trim_matches('"')` argument handling to shell
tails: quotes, whitespace, variables, pipes and redirection belong to the chosen
shell. Honor the user's PATH choice of Bash where available; if Windows needs a
WSL fallback, make the selected backend apparent rather than silently claiming
it is the same Bash environment. No new shell profile, SSH configuration or
service is installed or modified. Shell startup options must be documented,
particularly whether command mode loads user profiles.

## Recovery examples

A missing script needs more context than an OS sentence, without hiding that
sentence from a technical user:

```text
error: cannot read script 'sync.msync'
  path: D:\Projects\sync.msync
 cause: The system cannot find the file specified. (os error 2)
  help: Check the filename and parent directory. Relative paths use the current
        working directory; use /pwd in the REPL or pass an absolute filename.
```

For denied access, replace the missing-file advice with read permission and
parent-directory guidance. Do not prescribe administrator access for every
failure. For a directory supplied as a script, say that a file is required and
differentiate `/cd DIRECTORY` from `/load FILE`. For a missing transfer source,
keep the source token highlighted and offer path/alias inspection; do not assume
that creating an empty file is the correct repair.

Cancellation, expected empty state and syntax being typed are not failures.
Only submitted errors receive error treatment. Recovery messages should describe
an available next action rather than blame the user or merely append `/help` to
every failure.

## Acceptance journeys

1. Start the actual v0.3.1 executable in Windows ConPTY and a native Linux PTY.
   Observe different token styles while entering `root := $pwd::pwd` **before**
   Enter. Move the cursor, backspace and insert CJK/combining text without corrupting
   either display or submitted filename. Incomplete quoted/selector input does
   not panic, mutate the environment or emit premature errors.
2. Exercise `#` comments, adjacent URL fragments, operators inside quoted text,
   escaped quotes and selectors containing brackets/operators. Strip trusted
   styling and compare with original input; retain display-width/property tests.
3. Cause a syntax/namespace error and a missing script through CLI and `/load`.
   Check severity, source/path, useful native cause and a specific repair, then
   correct it in the same REPL. Distinguish permission and directory-as-file errors.
4. Bind a value, start/stop a real background flow, clear the screen, and inspect
   bindings/mode/cwd afterward. `/clear` has no data or environment side effects.
   Ctrl-C at an idle prompt and during an active flow both leave a usable session;
   physical flow handles close on the active-flow path.
5. Enter each available real shell, exit back, and verify the original binding and
   cwd. Run commands with spaces/quotes and commands that return nonzero. Exercise
   unavailable-shell recovery and reject shell entry before spawning while a job
   is active. Verify terminal input remains normal after success/failure/interrupt.
6. Run with auto/always/never, `NO_COLOR`, `TERM=dumb`, redirected stdout and piped
   stdin. Assert no interactive control sequences or prompts leak to plain mode;
   explicitly colored human messages have real ANSI; machine payloads still parse.
7. Preserve the existing real CLI/REPL/file/HTTP/SSH and interruption journeys.
   Sample every inventory category's normal/empty/error output, not only help.
   Keep captured plain compatibility prefixes and native cause context. Check
   long paths, spaces, Unicode, tabs and embedded control characters where an
   app-owned message interpolates a user-controlled value.
8. Verify `msync --version` is 0.3.1 and the deployed executable matches the
   tested artifact if this release is installed. Record actual PTY evidence;
   unit tests of a color function alone do not prove a highlighted live editor.

## Rationale and primary sources

- [Python's interactive-mode documentation](https://docs.python.org/3/tutorial/appendix.html#interactive-mode)
  describes the modern shell's editing/history/color support and the distinct
  interactive error, interrupt and stdout/stderr behavior. We adopt the recoverable
  interaction pattern, not a claim of full Python REPL feature parity.
- [Rustyline's Highlighter contract](https://docs.rs/rustyline/latest/rustyline/highlight/trait.Highlighter.html)
  explicitly requires equal display width and supports refreshing edited input.
  This is why post-submission colored echo is insufficient and formatting must
  not be mixed into the highlighter.
- [Rust compiler diagnostic guidance](https://rustc-dev-guide.rust-lang.org/diagnostics.html)
  separates a standalone primary message, source labels and supporting help.
  We retain that structure and native technical detail instead of replacing it
  with a generic product message.
- [Nielsen, CHI 1994](https://doi.org/10.1145/191666.191729),
  *Enhancing the Explanatory Power of Usability Heuristics*, grounds the focus on
  visible state, consistent operation, recognition and recoverability. It is a
  design framework, not empirical validation of this msync release.
- [NN/g error-message guidance](https://www.nngroup.com/articles/error-message-guidelines/)
  supports identifying the actual problem and an actionable remedy; its
  [hostile-error analysis](https://www.nngroup.com/articles/hostile-error-messages/)
  explains why ordinary edit states and empty results should not look like faults.
- [Command Line Interface Guidelines](https://clig.dev/#errors) connect readable
  human feedback with composable stdout/stderr and predictable automation.
- [PowerShell invocation documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh)
  is the authority for explicit interactive versus command invocation options.

## Lexical rendering follow-up (2026-10-30)

The display scanner now recognizes the REPL's established colon command aliases
(`:help`, `:pwd`, etc.), not only slash spellings. Static completion and unique
hints retain the user's chosen colon prefix; DSL operands followed by an operator
remain operands, and shell payloads remain outside DSL highlighting.

Contextual lookup items are included in the namespace-colored span for `$pwd`,
`$file`, and `$env`. For example, `$file::parent/assets` styles
`$file::parent`, leaving `/assets` as a path. Ordinary endpoint paths such as
`remote::configs/app` still style only `remote::`. This is lexical presentation,
not binding validation: no filesystem, network, compiler, or retained alias-state
lookup runs while typing.

Quoted operands preserve their public `String` token classification. Rendering
adds a cyan style to the grammar's recognized two-character escape pairs
(backslash, quote, `n`, `r`, `t`), while unknown escapes and an unfinished trailing
backslash remain ordinary green quoted text. Unquoted Windows backslashes are
not escapes. Stripping styles preserves the same display text, and the existing
editor control-character/width handling is unchanged. No public token enum
variants or execution grammar changed.

Verification on Linux: `cargo test --test highlight` passes three focused
regressions; all ten `repl_terminal` journeys pass, including a new real PTY case
that completes `:hel` with Tab, observes contextual-item and escape colors before
Enter, discards an unfinished contextual expression, and submits a Unicode alias
whose recognized and unknown escapes round-trip through `/bindings`. Existing
random-edit coverage and style-removal properties pass. Strict all-target Clippy
also passes. Windows ConPTY execution remains a separate platform check; no
Windows execution is inferred from Linux PTY results.

Native terminal assertions observe foreground-color semantics rather than exact
SGR parameter grouping. Windows ConPTY can emit `ESC[36m ESC[1m` where a Unix PTY
preserves `ESC[1;36m`; these render the same bold cyan. The added normalization
regression uses the actual native-stream shape. Direct highlighter tests still
check the intended bold/color sequences before terminal transport, while the
PTY journey verifies distinct command, namespace-item, known-escape and ordinary
string colors before Enter. Do not repair a transport-encoding assertion by
removing the real-terminal rendering checks.
