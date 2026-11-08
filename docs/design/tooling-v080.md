# v0.8 CLI and editor integration

The product entrypoint is `src/Msync.Cli/Program.cs`. Help/version/LSP dispatch
before configuration or telemetry initialization. Global options can precede a
subcommand, preserving established `--no-config run FILE` invocations. The
`config` command prints effective TOML and per-value origins without state
creation. Old explicit source versions and object extensions use explicit
baseline dispatch; the C# runtime never replays Rust debug source.

`compile`/`emit-ir` write the binary `Artifact` contract from Core. A named
artifact uses staged publication; stdout output contains bytecode only.
`bind-ir` freezes context, not filesystem metadata or authentication state.
`run` accepts source or either binary stage. Diagnostics preserve source-owned
line/column or bytecode operand provenance and the actual runtime cause chain.

## Session and editor state

`CompilerSession` retains binding slots, functions and constants while each
submission emits only its new entry instructions. `ExecutionSession` retains
entry locals; previous effects are never re-executed to reconstruct variables.
Incomplete brace/parenthesis/String submissions stay in the input buffer.
Functions and captured values remain usable in later submissions.

`LanguageServer` uses BCL JSON DOM parsing/writing and source-generated regexes,
avoiding reflection-based serialization and runtime code generation under
Native AOT. Content-Length counts UTF-8 bytes; .NET String offsets supply the
LSP UTF-16 coordinate domain. Incremental edits update only owned open-document
text. Diagnostics invoke the real compiler without interpreter effects.
Formatting calls the shared syntax `Formatter`, not a second grammar.
Completion/navigation index declarations and the embedded source modules;
standard definitions use `msync-stdlib:///0.8.0/MODULE.msync` virtual documents.
The client requests those exact bytes through `msync/stdlibSource`.

Native TextMate coloring is the fallback. Semantic URI tokens retain the
protocol, `://` operator, and complete address separately; quoted URI text stays
a String. Tokens spanning actual newlines split into legal single-line UTF-16
regions. Transport errors belong to stderr, never JSON-RPC stdout.

## Verification

`tests/Msync.EndToEnd/journeys.py` checks scope-held functions, option order,
literal-preserving/idempotent formatting, unpolluted binary stdout, and a real
interactive subprocess. It deletes a transferred output between submissions
and verifies later function calls do not recreate it.

Actual VS Code 1.144 production-host acceptance passed the main workflow
(`.temp/vscode-host-GwIHqG`) and URI workflow
(`.temp/vscode-host-T6kDXY`). The former produced 34 valid semantic tokens and
11 formatting edits, repaired a nullable diagnostic, navigated a generic
function and restarted the server without writing the transfer target. The
latter verified 74 tokens across ten URI forms. The editor README retains the
same procedure; later runners fingerprint adjacent managed assemblies as well
as apphost bytes.

`scripts/check_local.py` is typed local infrastructure. It builds the CLI with
strict static/AOT analysis, limits formatting inspection to changed C# files,
and runs only explicitly selected journeys. CI remains native cross-platform
and installation evidence, not an unrelated lint/publication quality gate.

The implementation follows the upstream
[LSP specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)
for framing, UTF-16 positions and incremental edits, and Microsoft's
[Native AOT restrictions](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
for static serialization and native deployment. Compiler/session ownership is
shared with the language contract rather than inferred from editor token roles.

## Native interactive terminal input

`InteractivePrompt` adapts [PrettyPrompt 6.0.5](https://github.com/waf/PrettyPrompt/tree/b7bbd3da5fbc1af53d1e0e6977ce43760a138ca1)
instead of implementing another terminal renderer. The editor owns cursor
movement, deletion, history navigation, multi-line rendering, selection and
clipboard interaction. It is created only for actual terminal input/output;
redirected input and `TERM=dumb` retain the plain submission reader. Help,
configuration inspection, script execution and LSP do not construct a prompt or
create its history file.

The adapter supplies pure lexical highlighting, compiler-owned binding names,
and local filesystem completion. Completing a pathname with spaces inserts an
explicit Path expression, not a bare String that would transfer literal text.
`$local::` and `$pwd::` remain local completion contexts; unknown/remote qualifiers
never enumerate, authenticate or probe an endpoint. Completion opens on Tab or
Ctrl+Space and commits on Tab/Enter. Trigger and commit are deliberately distinct
inside PrettyPrompt: binding Tab to both operations causes an open menu to
re-trigger rather than insert the selected item.

Enter inserts a soft newline only when the real source parser reports incomplete
input. Comments and regex characters cannot create a second brace-counting
grammar. Shift+Enter remains an explicit newline. Ctrl+C abandons the current
input, while an empty Ctrl+D/Ctrl+Z produces an explicit end-of-input result.
The returned cancellation token belongs to that submitted operation.

Input ownership is exclusive. Upstream `KeyPress.ReadForever` is a synchronous
iterator over `Console.ReadKey`, not a background reader. When `ReadAsync`
returns, that iterator is disposed and the library restores its prompt-scoped
terminal modes before the interpreter starts a native process or SSH client.
No keyboard producer can consume a later native credential or process response.
Only the cancellation owner remains active during execution.

History uses a versioned `history-v08` file in the selected history directory;
the new base64-line format does not overwrite a Rust history file. An unavailable
history directory reports its actual path/cause and leaves the in-memory editor
usable. `NO_COLOR` is honored by the upstream editor, and the adapter can request
plain highlighting/completion formatting without replacing console rendering.
PrettyPrompt and its TextCopy/embedded Wcwidth dependencies are Native AOT
compatible in the actual published executable; their source attribution ships
with the package.

`tests/Msync.EndToEnd/terminal_journey.py` creates a real Linux controlling PTY,
responds to terminal cursor-position queries, and drives editing rather than
submitting already-complete pipe lines. The original frozen `ca5c2e...` package
demonstrably ignored LeftArrow: Backspace then deleted the closing quote, leaving
the submission trapped in continuation input. Its retained terminal transcript is
`.temp/terminal-journey/1794129772127995865/terminal.log`.

The integrated native image
`6310460bfbed956b23b98147398487ca843cd02df6492168d679ee61157183f4`
passes cursor editing, UpArrow history, whitespace-path Tab completion,
Ctrl+C input recovery, comment-containing multi-line functions, foreground native
child stdin, Ctrl+C during child work, persisted history after restart and Ctrl+D
exit. The child writes a ready file before reading stdin; its exact received bytes
establish ownership independently of the prompt renderer. The same session's
12 warm submit-to-result samples had median 0.235 ms and nearest-rank p95
0.358 ms. The first prompt took 10.46 ms including process/PTY initialization.
The focused result is `.temp/terminal-native-full-results.json`; these terminal
observations are separate from the paired Rust/native workload benchmarks.

The final coherent CLI image
`c3c87eb1b644f9edfff225ee59306656ee2d46f6ad6c9ad990bd1004837ab377`
passes that actual PTY workflow after session recovery and cancellation ownership
were integrated. A cancelled native child leaves an already-executed String
binding usable and restores an older binding whose new Process-valued shadow
was never stored. A later prompt still calls the earlier multi-line function.
Its first prompt took 12.724 ms; 12 warm submit-to-result samples had median
0.219 ms and nearest-rank p95 0.334 ms. The retained report is
`.temp/terminal-full-final.json`.

Three focused selections independently pass on the same image:
`--case qualified-completion` copies the exact bytes of a whitespace pathname
through both `$local::` and `$pwd::`; `--case history-access` reports an unusable
history path while preserving the unrelated file and retaining a working editor;
`--case color-policy` honors configured `color = "never"` without relying on
`NO_COLOR`. Reports are `.temp/terminal-{qualified,history,color}-final.json`.
The default history path is `~/.msync/history-v08`, independent of transfer scratch
configuration.

Submission returns the editor's actual source buffer captured at the configured
submit key, not PrettyPrompt's host-newline-normalized presentation result.
This preserves multi-line String data and exact parser-cache identity on Windows.
All submit bindings use the same `KeyBindings` instance as the editor; a
completion commit or soft newline cannot be mistaken for a source submission.
