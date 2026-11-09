# v0.8 CLI and editor integration

The product entrypoint is `src/Msync.Cli/Program.cs`. Help/version/LSP dispatch
before configuration or telemetry initialization. Global options can precede a
subcommand, preserving established `--no-config run FILE` invocations. The
`config` command prints effective TOML and per-value origins without state
creation. Explicit legacy source dispatch remains separate; the C# runtime never
replays Rust debug source. Current 0.8 bytecode is format 4 only: old binary
formats produce a source-recompilation diagnostic, not a compatibility decoder.

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

## Language upgrade presentation

The source UI distinguishes syntax from inferred declaration roles. `scope` and
`fn` remain keywords; named Scope declarations and named standard modules use
the type role, callable names use function, and anonymous Scope bindings use
variable. Concepts and their constraint references use type. `as` shares the
control-keyword role with `if`, `else`, `match`, postfix `?` and ordering fences;
`@` is an operator. `global` remains a keyword with lexical-ancestor completion.
Semantic roles augment rather than replace the source grammar, following the
[VS Code semantic-highlighting model][semantic-highlighting].

Physical qualification is `path @ connection`; lexical member access remains
`scope::member`. All prompt, source-LSP, syntax-reference and snippet examples
must use the same contract. Number token presentation consumes the source
lexer's numeric spans rather than reclassifying arbitrary digit-prefixed paths.
The semantic-token stream merges those spans linearly without sorting another
token collection. Physical completion consumes compiler flow context; obsolete
Connection `::` resolver chains are removed rather than kept as a second path.
The `@` inside a native SSH authority remains URI data, not qualification syntax.
Runtime value display follows the same direction: remote Path values print
`raw @ ssh://alias` or `raw @ wsl://distribution`, while local paths retain their
native spelling without a synthesized `$local`. This is presentation only:
`Path as String` still projects `NativeText`, not the endpoint-qualified display.

The progressive reference exposes Number, `as` and concepts below `language`.
Library topics derive their module set from the embedded executable source
resources, excluding private underscore-prefixed modules. New source modules
therefore appear in the skill without a second manually maintained module list.
The removed public cast function is not advertised as a module or root export.

Formatting borrows readability mechanisms, not grammar, from
[PEP 8][pep8] and [Black][black-style]: a soft 80-column target, consistent
leading binary operators when an expression wraps, hanging indentation, and
paragraph separation around declarations. Braces and semicolons retain their
language semantics. Indivisible String/Path/regex data and comments take priority
over a hard line budget; deeply nested literals are not rewritten to fit.
The formatter uses parsed source spans to distinguish numeric division from
path joins and maintains its output column incrementally; it does not rescan
the accumulated output for each token. `tests/Formatting` isolates the real
syntax implementation for numeric/cast/concept/operator layouts, nested
80-column behavior and 1,000 arbitrary UTF-16 literal round trips. The real
`formatter_journey.py` exercises the installed command and REPL-facing grammar.

The upgrade's focused actual-process entrypoint is
`tests/Msync.EndToEnd/language_upgrade_tooling_journey.py --binary PATH`. It
exercises semantic roles, concept hover/definition/constraint completion, named
standard modules, unary-name replacement, persistent REPL concepts and actual
PTY rendering (ConPTY on Windows). Constraint-only concept names are offered in
type positions, not as runtime variables; the REPL obtains them separately from
`CompilerSession.DescribeConcepts`. Scope path completion is checked against
actual local and retained native SSH directory reads, including Connection
fields reached through named Scope aliases.

`tests/PromptPresentation` links the product highlighter and checks 266 source
character/style observations. Its optional `--measure` path warms up 1,000
calls and measures 10,000 repetitions of an ordinary 78-character input.
The accepted managed Debug observation was 0.0389 ms and 6,280 allocated bytes
per call; this is a component observation, not Native AOT or end-to-end latency.
It supports keeping the single shared lexical pass rather than introducing a
separate prompt token grammar or an incremental cache without measured need.

[semantic-highlighting]: https://code.visualstudio.com/api/language-extensions/semantic-highlight-guide
[pep8]: https://peps.python.org/pep-0008/
[black-style]: https://black.readthedocs.io/en/stable/the_black_code_style/current_style.html

## Read-only binary artifact documents

`.mso` and `.mexe` are binary program artifacts, not source documents. Their
inspection path uses the same Core `Artifact.Read` implementation as the CLI and
runtime. The viewer never binds, optimizes, executes or authenticates an
artifact. It presents the decoded stage, actual binary format version, language
version, optimization level, semantic flags, captured context, typed constants,
functions and basic blocks. Instruction operands name their semantic roles
rather than presenting unexplained integer A/B columns. Optimized-away source
is not reconstructed or replayed.

`msync disassemble FILE` produces the readable IR view; `--json` produces a
versioned presentation model for tooling. This dispatch precedes configuration
and telemetry initialization. The presentation schema version is independent
of the artifact format and language-semantic versions. Source spans, when
present, are navigation metadata rather than a substitute for decoded code.
Internal function, block, constant and local references remain navigable after
the original source is deleted. Relative debug labels do not justify guessing
an absolute source location.

The extension uses a native `CustomReadonlyEditorProvider` for binary files and
a `TextDocumentContentProvider` for the optional normal read-only IR editor.
The custom view supplies a themed overview and navigation; **Open IR as Text**
supplies ordinary editor selection, outline, hover and definition behavior. The
virtual scheme is `msync-artifact` and its language is `msync-ir`, deliberately
outside the source-language server's `msync` document selector. Opening IR must
not invoke the source parser or format the original bytes as text.

The `msync/artifact` request takes `{ uri, data }`, where `data` is Base64 file
content read by `workspace.fs`, and returns schema version 1 with `text`,
`formatVersion`, `languageVersion`, `stage`, `optimizationLevel`, `symbols`,
`references` and `hovers`. The JavaScript client does not decode a parallel
binary format. Reading bytes through the workspace filesystem preserves remote
and virtual filesystem ownership; the native server treats the URI as an origin
label and does not interpret it as a path to open on a different machine.

Decoding and rendering run outside the protocol event loop. New inspection for
the same URI supersedes older work; explicit LSP cancellation and shutdown
cancel pending requests, and late results cannot publish after cancellation.
Corrupt/unsupported artifacts return an actionable inspection error while the
same server can continue serving source and artifact requests. Refresh and
language-server restart invalidate presentation snapshots, never modify the
binary or execute its program.

All displayed strings, including paths, constants and debug labels, are data.
The webview escapes them and uses a content-security policy; source navigation
opens safe documents only after an explicit user action, never a `command:`
URI from artifact metadata. Markdown hover content remains untrusted.

The split follows the official [custom-editor API][custom-editors] and
[virtual-document API][virtual-documents]: binary ownership belongs to the
custom document, and derived IR belongs to a read-only content provider. It
does not use a writable text document as a disguised binary editor or rely on
redirecting and disposing a custom editor during resolution.

[custom-editors]: https://code.visualstudio.com/api/extension-guides/custom-editors
[virtual-documents]: https://code.visualstudio.com/api/extension-guides/virtual-documents

Focused acceptance is split by ownership:

* `tests/Msync.EndToEnd/artifact_disassembly_journey.py --binary PATH` exercises
  CLI text/JSON/color/stdin, both artifact stages with source removed, inertness,
  malformed files and explicit broken-configuration bypass.
* `tests/Msync.EndToEnd/artifact_view_journey.py --binary PATH` exercises the
  actual protocol, remote filesystem labels, malformed-payload recovery,
  cancellation concurrent with normal source hover, and navigation metadata.
* `MSYNC_TEST_ARTIFACT_ONLY=1 MSYNC_TEST_SERVER_PATH=ABS_PATH node
  editors/vscode/test/integration/run.cjs` selects the real extension-host journey
  without repeating the existing source-language suite.

The VS Code 1.144 host journey opens both binary custom editors and derived text
views, navigates symbols/definitions, checks hover, refreshes after recompilation
and repair, and restarts the server. Read-only behavior is tested through the
actual user `type` command together with `isDirty` and binary hashes: privileged
`TextEditor.edit` is not a user-editability oracle for virtual documents. An
absolute effect-marker path detects accidental execution independently of server
cwd. Script-looking literals and a valid debug span containing a `command:` URI
remain inert data, rather than requiring a test-only product hook.

### Initial atomic replacement and watcher ownership

File-scheme artifacts use synchronous Node directory notifications registered
before the first byte read. One non-recursive watcher is shared per parent
directory; an index filters notifications to the visible artifact filenames.
Watching the directory, rather than the original file inode, follows the
compiler's owned-stage atomic replacement. Temporary staging filenames do not
cause unrelated artifact reads. Non-file providers retain VS Code's filesystem
watch API, and native notification errors report their cause and fall back to
that provider. Byte reads themselves still use `workspace.fs`.

This closes an observed first-open race, not an optimization based on a guessed
timeout. With the final Native AOT decoder, two independent fresh VS Code 1.144
hosts saw the compiler publish generation two and the OS report its rename,
while the IR document remained on generation one for the full observation
window. Explicit refresh decoded generation two immediately. A subsequent
atomic compile refreshed automatically, with no intervening sleep or document
close, distinguishing initial watcher registration from reader or model-cache
failure. The [VS Code watcher internals][watch-internals] explain why a
non-recursive RelativePattern inside the workspace may depend on an existing
recursive workspace watcher; the stable API exposes no readiness promise.

After the notification change, the independent host on the identical Native
binary observed the first atomic replacement automatically, followed by ordinary
corruption and repair writes without manual refresh. Existing source editing,
restart, navigation, inert metadata and byte preservation also passed. There is
no polling interval, added sleep, recursive scan or duplicate local watcher.

[watch-internals]: https://github.com/microsoft/vscode/wiki/File-Watcher-Internals

Native TextMate coloring is the fallback. Semantic URI tokens retain the
protocol, `://` operator, and complete address separately; quoted URI text stays
a String. Tokens spanning actual newlines split into legal single-line UTF-16
regions. Transport errors belong to stderr, never JSON-RPC stdout.

## Responsive document processing

LSP input, analysis and publication have separate ownership. A buffered input
reader emits messages to one protocol event loop; only that loop updates open
documents or writes protocol frames. One compiler worker consumes the latest
pending snapshot per URI. Replacing a pending snapshot does not enqueue another
copy, and different documents retain independent pending work. No debounce sleep
or arbitrary source-size cap is needed to keep completion and shutdown responsive.

Each open document retains immutable source text, its client version, a
monotonically increasing internal generation and a line-start index. Completed
diagnostics publish only for that same generation, including across close/reopen
where client versions may repeat. Published diagnostics carry the client version;
stale incoming versions do not replace newer source. Shutdown stops admission
without waiting for an already-running pure compiler call.

The line index makes token/symbol range conversion logarithmic instead of
rescanning from the start for every token. UTF-16 offsets, LF, CRLF and lone CR
share this representation. Semantic-token enumeration uses BCL ValueMatch spans
rather than per-token Match/Group graphs, and numeric arrays use source-generated
JSON metadata. Protocol output writes UTF-8 directly through Utf8JsonWriter,
avoiding a complete intermediate UTF-16 JSON string and transcoding copy.

The actual stdio lifecycle journey is
`tests/Msync.EndToEnd/lsp_analysis_journey.py`: superseded analysis, stale versions,
close/reopen identity, multiple documents, astral text, line endings, exact source
diagnostic spans, and shutdown with analysis pending. The latency companion runs
real native processes with 1,000/4,000-line documents and sixteen successive edits.

The ownership model follows [Roslyn immutable snapshots](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace)
and the [LSP versioned diagnostic contract](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#publishDiagnosticsParams).
[BCL Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
provide the native producer/consumer mechanism. The relevant insight from
[Lamport's event ordering](https://lamport.azurewebsites.net/pubs/time-clocks.pdf)
is causal identity rather than wall-clock delays: completion of old work is not
authority to replace a newer document. This server is not a distributed-clock
implementation or a claim of incremental type inference.

### Native latency evidence

The preserved 0.8 delivery baseline is native image `ca5c2eb2...`; the final
product candidate is `b89d9da5687c8ef526e7659f1c99e53cb21beb03f16305d776e94f07dbc39794`.
The maintained latency runner freezes the complete adjacent payload and records
hashes. These are real stdio measurements on the same Linux host, not isolated
provider calls or performance thresholds in CI.

| Actual request/workload | Delivery baseline ms | Responsive snapshot ms |
| --- | ---: | ---: |
| 1,000-line semantic tokens | 60.659 | 7.862 |
| 4,000-line semantic tokens | 832.186 | 23.830 |
| 4,000-line open then hover | 145.376 | 1.437 |
| Sixteen edits plus completion, 4,000 lines | 2663.783 | 113.358 |

The first source-index/coalescing pass still took 74.601 ms for the 4,000-line
token response. Replacing Match/Group allocations with ValueMatch enumeration
and writing UTF-8 directly reduced that to 23.830 ms. Source-generated integer
array JSON preserves the protocol shape without runtime reflection. The burst
comparison also includes the independently measured compiler improvements; it
must not attribute all reduced work to the protocol loop alone. A large response
still costs time proportional to its token/output count.

Raw records are `.temp/product-latency/lsp-before.json`,
`lsp-after-final.json` and `lsp-after-frozen.json`. The final native lifecycle
journey observed 1.496 ms for hover following superseded analysis and 2.180 ms
for shutdown while analysis was pending, and verified versions, source spans,
close/reopen identity and multiple-document progress.

## Finite source ingestion

Ordinary source files use synchronous File.ReadAllText before parsing, because
there is no independent work to overlap with that first read. The private helper
returns the completed String in a ValueTask without allocating a fake Task;
streamed stdin retains ReadToEndAsync. Existing decode/BOM/newline semantics and
actionable native read errors are unchanged, including already-consumed legacy
stdin forwarded to the Rust implementation.

The same Native AOT component image, alternating 31 fresh processes per mode on
Linux, measured 4 KiB sync/async p50 7.864/9.512 ms and p95 9.088/10.710 ms;
64 KiB p50 was 8.362/10.461 ms. This isolates first-file asynchronous machinery,
not a comparison of managed versus native code or different guest compilers.
The actual source-input journey verifies a UTF-8 BOM, long Unicode data, literal
CRLF, streamed stdin, missing/directory inputs and real legacy stdin. Its native
candidate identity is `b89d9da5687c8ef526e7659f1c99e53cb21beb03f16305d776e94f07dbc39794`.

```sh
python3 tests/Msync.EndToEnd/source_input_journey.py --binary /path/to/msync --legacy /path/to/legacy-msync
```

Windows source-read modes run in the native platform comparison; Linux timings
are not relabeled as Windows results. End-to-end CLI comparisons and the probe's
full input/binary hashes belong to the focused performance note.

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

## Actual native Windows console acceptance

`tests/Msync.EndToEnd/windows_terminal_journey.py` drives the Windows console
through the native ConPTY backend of [PyWinPTY](https://github.com/andfoy/pywinpty),
not redirected standard input or a replacement Win32 console implementation.
The fixture pins PyWinPTY 3.1.2's official CPython 3.14 x86-64 wheel by SHA-256
in `windows-terminal-requirements.txt` and selects backend zero explicitly.
Its command is:

```text
python -m pip install --require-hashes --only-binary=:all: -r tests/Msync.EndToEnd/windows-terminal-requirements.txt
python tests/Msync.EndToEnd/windows_terminal_journey.py --binary PATH/TO/msync.exe --output .temp/platform-results/windows-console.json
```

The actual Windows Server 2025 console passed all **ten** phases in
[job 128711880542](https://github.com/kleedaisuki/msync/actions/runs/42474236661/job/128711880542):
Left/Backspace/End editing; UpArrow history; wrapped whitespace-path Tab completion
and byte-verified transfer; Ctrl+C input cancellation; brace/comment continuation;
ordinary Enter inside an unterminated String producing exactly `first\nsecond`
bytes followed by a retained function call; native child stdin ownership; Ctrl+C
during native child work with the function still callable; Ctrl+D EOF; and history
restoration in a newly launched process.

This used runner image `win25-vs2026` version `20261103.298.1`, Python 3.14.10,
and the unchanged Native AOT executable SHA-256
`40d37d193439bde5263bbc6b0e152c0aed89b0efee736e400a41f7fc04bf0e79`
from the preceding native package run. The native process was not rebuilt while
correcting the test's input driver. First prompt took 72.424 ms including ConPTY
creation; five warm submission observations were 7.323–7.795 ms (median 7.547 ms),
including socket/console transport and output observation. Paired product latency
comparisons are recorded separately in the performance note.

The real traces exposed two input-driver distinctions. ConPTY's terminal BS byte
selects Ctrl+Backspace (word deletion); DEL is plain Backspace. Also, sending Enter
after merely the first output fragment can leave printable text queued, which
PrettyPrompt legitimately batches with the CR as a paste rather than a submit.
The fixture now waits for the complete printable paint before the distinct Enter,
ignoring physical wrap line feeds. Exact result and file-byte assertions remain;
no fixed sleeps, added production branches or relaxed cancellation checks were
needed. Raw VT logs and completed phases accompany the JSON result. This console
phase passed independently of the separate WSL-download failure in that job.

## Source-oriented language presentation (0.8.0 polish)

CLI help, the short syntax guide (`msync help syntax` or REPL `/syntax`),
interactive highlighting, bytecode plans, and diagnostics share a theme-adaptive
16-color vocabulary. Ordinary identifiers, parameters, anonymous Scope values,
unbound path text, endpoint authorities and structural punctuation inherit the
terminal's default foreground. Cyan marks keywords, protocols and operators;
green marks Strings; magenta marks types, control flow (including `?`), numeric
literals and regex patterns. Known callable identifiers use yellow, and named
Scope declaration names and their aliases retain the type role. Comments and
inert hints are gray. The `fn` and `scope` tokens remain keywords in every
declaration form. Source gutters use blue, repairs use green, and actual primary
errors use bold red. Colors come from the terminal's own palette rather than a
fixed RGB background. With `auto`, detection follows the rendered stream. Piping
stdout does not disable color on terminal stderr; terminal stdout does not color
piped stderr. `always` explicitly emits SGR; `never`, `NO_COLOR`, and automatic
`TERM=dumb` disable it. Source formatting, binary bytecode, TOML configuration,
LSP frames, and ordinary program values remain undecorated.

This separation follows the token-role design used by
[fish](https://fishshell.com/docs/current/interactive.html#syntax-highlighting)
and [PSReadLine](https://learn.microsoft.com/en-us/powershell/module/psreadline/set-psreadlineoption#-colors),
not a copied RGB theme. The earlier scanner colored every unmatched atom blue,
every known variable bright blue, and nearly every delimiter yellow. That made
ordinary input dominate the accents and treated identifiers as if they were all
filesystem literals. Leaving normal text unstyled also lets light and dark
terminal themes choose their intended foreground. Highlight spans never alter
source bytes or cursor geometry; the existing PrettyPrompt renderer still owns
painting, selection, wrapping and ghost-text placement.

Command help is selected from the ordinary parsed command, not by searching raw
argument strings: a configuration filename such as `fmt` cannot redirect
`msync --config fmt run --help` to formatter help. Both help and skill reference
requests return before configuration discovery and telemetry initialization.
The REPL lists real editing keys, continuation behavior, persistent bindings,
and the distinction between a returned block value and semicolon discard.
The syntax guide explains the otherwise easy-to-confuse points: bare unbound
names are Paths, Scope member selection is not Connection qualification, and
postfix `value?` means **is null**, not is-present.

Compiler diagnostics are structured source data, not a location prefix embedded
in a sentence. Terminal presentation renders primary and related source frames,
labels, notes, actionable help, and the proposed edited line. Related frames
explain generic call specialization without hiding the source-owned failure.
CRLF/LF, tabs, combining characters and emoji use UTF-16 source offsets but
terminal-cell underlines. The existing PrettyPrompt grapheme-width implementation
provides cell measurement; no second Unicode width table is introduced. Long
lines have a viewport around the relevant span; long multiline expressions retain
their opening and closing highlighted lines. Full native causes remain visible,
with identical adjacent wrapper messages collapsed. Entire error frames serialize
only on the failure path, so parallel transfer failures cannot interleave frames.

Runtime transfer reporting uses an optional `ExecutionOptions.ReportError`
init-only callback. The positional constructor/deconstruction contract and the
existing writer fallback for library consumers remain unchanged. Transfer errors
still produce Bool false and increment the same failure count. Direct source
execution can show its retained source snapshot. Source-free bytecode and runtime
REPL functions show retained operand source names and offsets instead: bytecode
format 2 does not contain source text, and an earlier REPL function can share
`<repl>` with a different submission. Presentation never rereads a coincidentally
named file from the current directory or invents a source line from an offset.

These choices follow the [Rust diagnostic structure guidance](https://rustc-dev-guide.rust-lang.org/diagnostics/diagnostic-structs.html)
and [GCC diagnostic guidelines](https://gcc.gnu.org/onlinedocs/gccint/Guidelines-for-Diagnostics.html):
explain the user's source operation, retain meaningful locations, and offer a
repair rather than exposing only an internal type-checking failure.

### Inference-backed editor surfaces

The compiler records editor observations only when editor analysis requests them;
ordinary compilation does not construct semantic descriptions. Descriptions are
deferred until the completed inference result is available, so a function hover
can show its actual parameter constraints and selected return types. Aliases and
Scope members retain their inferred callable/Scope identities. Function-valued
identifiers use the standard `function` semantic token, and Scope values use
`type`, including inferred declarations and member references. The extension's
TextMate roles remain the fallback while analysis is pending.

Analysis remains a versioned asynchronous snapshot cache. A supported client
receives one semantic-token refresh request after new inference is published;
the server consumes the response rather than treating it as another request.
Hover waits only for its own generation, not by blocking the protocol loop.
Cancellation, a newer version, close/reopen and shutdown retire obsolete pending
requests. Completion includes useful member lists and type/signature details;
hover no longer returns a synthetic “typed expression” placeholder. Quick fixes
intersect the diagnostic span and apply a versioned document edit. Formatting
invalid/incomplete source returns no edit rather than terminating the server.
The contracts follow the [VS Code semantic highlighting guide](https://code.visualstudio.com/api/language-extensions/semantic-highlight-guide)
and [LSP 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/).

Formatting uses an 80-column target, not a rejection threshold. Calls and
expressions break at syntactic boundaries; deeper indentation reduces available
space, but indivisible Strings, regexes and path spellings remain intact. This
is the same useful distinction between print width and maximum line length made
by [Prettier's print-width guidance](https://prettier.io/docs/options#print-width).

### Progressive built-in reference

`msync skill` prints a compact root index; `msync skill language functions` and
other child paths reveal focused pages. `SkillReference` owns the immutable topic
registry and embedded Markdown, including immediate-child and parent commands.
Standard-library pages show declarations from the same embedded source bytes
used by the compiler, rather than another handwritten API inventory. Reference
requests do not read user files, initialize telemetry, or invoke native transports.
Automatic pipeline output is plain Markdown; explicit `--color always` adds only
SGR styling, preserving fences, backticks, text and newline bytes after stripping
SGR. The language/product version stays 0.8.0.

Focused real-process journeys are `presentation_journey.py` (CLI and LSP),
`terminal_presentation_journey.py` (nine actual POSIX terminal/pipe cases),
`windows_presentation_journey.py` (native ConPTY), `formatter_journey.py`, and
`skill_journey.py` under `tests/Msync.EndToEnd`. The skill journey traverses all
18 pages, checks 26 executable example blocks, and performs a real documented
copy. The terminal fixture distinguishes SGR styling from .NET's independent
terminal application-mode initialization, and compares content across automatic,
forced, disabled, and `NO_COLOR` modes without assuming one terminal's palette.

### Contextual filesystem and endpoint completion

`SourceCompletion` owns the lexical replacement region and literal encoding used
by both LSP and the REPL. Local candidates come from the selected directory's
native enumeration, using the editor/session's captured working directory and
home, not the source file's parent directory. A candidate without whitespace is
a Path expression with literal components; quoting those components prevents a
same-named variable from silently changing which file is selected. A candidate
containing whitespace is a String literal, with msync's doubled-quote escaping,
except when it is the source operand before `->`: filesystem completion there
retains Path so it copies the selected file rather than its filename text.
Already quoted input remains a String, preserving explicit literal intent. A physical Connection qualification
retains its source spelling and yields Path even when its right operand is a
String. Completion replaces the complete token, including an existing closing
quote or a stale suffix after the cursor; UTF-16 LSP ranges do not split astral
characters. Descriptions distinguish Path from String and explain that a String
on the left of `->` writes literal text; a Path annotation selects file contents.

The SSH host catalog reads concrete Host aliases, recursive Include files and
remembered known_hosts names. It honors the runtime's explicit
`MSYNC_SSH_CONFIG` override, and preserves the origin of each suggestion.
Wildcard and negated Host patterns are not invented as concrete hosts. Include
paths use [OpenSSH's documented user/system roots and lexical glob order](https://man.openbsd.org/ssh_config#Include),
not the parent of an arbitrarily nested included file. Catalog discovery does
not run Match commands or authenticate.

Selecting a qualified SSH or WSL path is different: its completion performs an
actual directory query on that endpoint. `SourceCompletionService` owns retained
native channels through `RemoteDirectoryCompletion`; each explicit query sees a
fresh directory listing, while subsequent queries reuse the same authenticated
channel. Remote HOME, PWD, slash rules and directory flags come from the selected
endpoint, never from a local projection. The [retained channel contract](editor-remote-completion-v080.md)
describes NUL framing, filenames, cancellation ownership and native diagnostics.
Opening or type-checking a document still creates no connection. Authentication
belongs to the admitted completion request and is cancelled with that owner.

### Native prompt lexical roles and inline hints

The 0.8 prompt now covers the display roles previously present in the Rust
editor: Paths, protocol/address segments, operators, pattern selectors, comments,
Strings and type names, plus known Function and named/anonymous Scope roles. A tolerant
linear display scanner handles incomplete edits, doubled String quotes, escaped
regexes, character classes and named groups. It does not accept a program or
replace the execution parser. Hint context skips Strings, comments and regex
bodies rather than proposing language keywords inside data.

`CompilerSession.DescribeBindings()` describes the accepted/reconciled semantic
environment once before a new prompt. Canonical builtins come from the compiler's
lazy cached descriptors of the same embedded standard source, including source
wrappers rather than only native IDs. Keystroke highlighting and static hints
use that snapshot; neither compiles the input nor enumerates files or endpoints.
An explicit Tab request may inspect the current submission with
`CompilerSession.AnalyzeEditor(...).At(caret)`: its isolated compiler view retains
lexical blocks, parameters, shadowing and computed receivers without executing
or mutating the accepted session. Completion menus reuse that request snapshot
while the user narrows a prefix, instead of recompiling on subsequent keys.
Unique keyword/binding/member suffixes precede the existing native history-prefix
suggestion. Otherwise noninsertable bracketed context explains an expected value,
transfer destination, null check, Scope member list, or function signature and
current argument. RightArrow or End at the document end accepts an insertable
suffix. Enter always submits only the typed/accepted document; a displayed hint
is not clipboard text, history, a binding or executed source. `NO_COLOR` and
`--color never` disable inline hint presentation, matching the legacy policy.

PrettyPrompt 6.0.5 has no public inline-hint callback. Its exact pinned source is
therefore retained in `vendor/PrettyPrompt`, with the narrowly documented local
extension in [the origin note](../../vendor/PrettyPrompt/ORIGIN.md). Hints use its
existing pooled screen cells, grapheme widths and incremental screen diff, not
another terminal renderer. The original constructor and default behavior remain
valid; hinting is opt-in. Native packages include the modified MPL-2.0 source.

Tab completion uses the shared asynchronous source-context provider. During a
selected remote directory query, Ctrl+C cancels that query without ending the
session. Native SSH authentication temporarily borrows the terminal only while
the editor's completion callback is awaited, so the editor is not concurrently
reading keys. The native renderer suspends, then reanchors/repaints the original
buffer after the owned askpass interaction. Password text never enters source or
history. Completion failures use complete source-independent diagnostics even
when color is disabled, rather than disappearing with gray hints.

The focused actual-PTY journey `tests/Msync.EndToEnd/hint_journey.py` verifies
keyword/history/binding acceptance, nonaccepting Enter, source/result byte
consequences, lexical colors, no-color behavior and native child stdin ownership.
The distinct native ConPTY fixture is
`tests/Msync.EndToEnd/windows_hint_journey.py`; it uses the same known-binding
contract without inferring Windows behavior from POSIX tests. Neither fixture
requires per-keystroke filesystem probes or repeats the earlier full editor matrix.

Semantic completion uses `EditorAnalysis.At(offset)`, not declarations found by
whole-file regular expressions. The compiler retains immutable lexical snapshots
for blocks, function definitions, specializations and incomplete expressions;
the context selects the innermost visible bindings and the actual receiver before
`::`. Parameters, captured bindings, local shadows, anonymous functions, nested
Scopes and Scopes returned by calls therefore share normal type inference.
An unknown generic parameter remains explicitly unknown; multiple concrete
specializations expose only common safe members. An editor probe can retain
useful types through missing delimiters without admitting that incomplete program
as executable input. REPL completion uses the same editor analysis fork seeded
from accepted session bindings, rather than replaying earlier effects.

Path completion separates the lookup value from the edited expression. Pure
compiler facts identify literal Paths, String bases, joins, qualifications,
Scope fields and known returned Paths without executing them. A lookup of
`folder/pa` can therefore enumerate the known value of `folder`, while inserting
`folder/"paper.txt"` rather than treating `folder` as a directory name. The same
contract covers `Settings::root/` and `factory()/`. Unknown runtime values are
not fabricated as same-spelled filenames. In a live REPL, accepted binding facts
come from the owned initialized VM slots: Path working-directory/home captures
and nested Scope fields survive `/cd`. Function captures are matched in closure
capture order, not by misusing a captured slot as a root slot; the editor fork
never mutates the live compilation or invokes the function to obtain a path.

## Version-owned response data and completion documentation

Semantic-token responses retain their source-generated encoded integer array in
an immutable `JsonElement` on the exact `EditorDocument` generation. Each reply
gets a fresh lazy `JsonArray` wrapper, not a shared mutable JSON node or a second
lexical/semantic pass. A new document snapshot owns a new cache, including
close/reopen with repeated client version numbers. Declarations use parser and
compiler occurrences already published for that generation; a second
whole-document declaration regex is not a competing source of semantic roles.

Clients advertising `completionItem.resolveSupport.properties` containing
`documentation` receive completion labels, kinds, inferred signatures and all
insertion/filter fields immediately, with documentation deferred until
`completionItem/resolve`. Other clients retain eager documentation. The opaque
item data contains the internal generation and query offset; resolution selects
that same generation's lexical/member context and original label. It never
executes source, authenticates an endpoint or reads the filesystem. An edited,
closed or reopened document cannot cause an old item to resolve against new
bindings; an unavailable old generation leaves the item unchanged.

Path candidates keep their insertion semantics and eager String-versus-Path
safety guidance. Resolving a semantic candidate adds only documentation; it does
not change the label, kind, signature, text edit, filter text or opaque data.
This follows the [LSP completion and resolve contract](https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/language/completion.md),
which explicitly supports lazy documentation while preserving insertion and
filtering fields. The encoded token array uses the AOT-compatible
[`SerializeToElement` overload with generated `JsonTypeInfo`](https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializer.serializetoelement)
instead of runtime reflection.


## Editing-time document symbols and keyword roles (2026-11-09)

A `textDocument/documentSymbol` request can arrive before semantic analysis has
finished. A missing signature is represented by an omitted `detail`, not JSON
`null`. The installed vscode-languageclient 11.0.0 first tests
`DocumentSymbol.is(first)`: its optional detail is `undefined | string`. A null
detail rejects that union branch, after which the client interprets the same
object as `SymbolInformation` and dereferences the absent `location.range`.
The resulting TypeError was a malformed server response, not an editor range
that should be swallowed or repaired by client middleware.

The server now negotiates `hierarchicalDocumentSymbolSupport`: true returns
`DocumentSymbol` objects with `range` and contained `selectionRange`; false or
absent returns `SymbolInformation` objects with a real `location` URI/range.
Unknown optional fields are omitted in either representation. The contract
follows the [LSP document-symbol union](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#documentSymbol),
and matches Microsoft's [null-omitting optional detail](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.languageserver.protocol.documentsymbol.detail).

The actual VS Code 1.144 production bundle exercised immediate document-symbol
requests across three incomplete/complete edits of a 5,001-declaration document
without waiting for diagnostics, retaining the real client conversion path.
That managed-image host passed at `.temp/vscode-host-sD3rYu`; the portable
regression runner additionally feeds actual server arrays through the installed
protocol classifier and client converter for hierarchical and flat clients.

Presentation separates tokens from the values they declare: `fn` and `scope`
use the keyword role; declaration names, aliases and references retain inferred
function/type/variable roles. Control-flow `if`, `else`, `match` and `?` keep
their independent control role. This rule is shared by semantic tokens,
TextMate fallback and the terminal/guide scanner; keyword tokens cannot be
overridden by an enclosing inferred expression.

## Editing-time source preservation (2026-11-09)

Completion edits own the current item, not the containing expression. A selected
ordinary filename preserves the user's path prefix, endpoint, separators and
manual quote context. Only syntax-significant names, whitespace or an actual
binding ambiguity require escaping. Completion never introduces `$local` or
quotes every normal component. Lookup paths retain their independent captured
cwd/home/endpoint facts; those facts are not source-rewriting instructions.

`$` is an operator. The shared builtin provider replaces only its operand, so
`$en` selecting `env` produces `$env`, never `$$env`. Its root catalog contains
`std`, `local`, `wsl`, `pwd`, `file`, `path` and `env`. Canonical range operations
are members of `$std::ranges`; former view/range convenience roots are absent
from source and public completion. Current binary readers accept format 4;
older artifacts require source recompilation.

Environment and PATH completion query the actual process context. `pwd` and
`file` combine their real special members with correctly projected physical
entries; file-relative lookup uses the document's source directory, not a
convenient fallback cwd. Unknown path values do not become guessed directory
names. Incomplete Windows UNC authorities are editing prefixes, not directory
queries; complete native paths retain useful native failure diagnostics.

Legal Atom names such as `.codex` resolve through the same lexical environment
as other names. Unbound names retain their Path meaning. Parameter and return
constraints share the canonical type catalog. The return header `-> Type` is
not the transfer operator context, and the REPL hint reflects that distinction.
Definitions and outlines use parser declarations and actual binding origins,
never declarations guessed from text inside comments or strings.

## Source-backed library editor acceptance

`MSYNC_TEST_STDLIB_ONLY=1` selects the focused actual VS Code host journey in
`editors/vscode/test/integration/run.cjs`. As with the artifact journey, callers
must supply an absolute `MSYNC_TEST_SERVER_PATH`; the runner records the server
hash and any managed assembly hashes rather than selecting a Debug or PATH
server implicitly. The suite follows cast, Range, String and functional
compositions into the installed `msync-stdlib` virtual documents, checks their
selected definition ranges and inferred types, and completes structured Range
elements. This is independent of the existing source and artifact host suites,
so a library-only change does not repeat unrelated editing or binary workflows.

## Standard concepts, associated types and named families

The type namespace is distinct from runtime values. Standard constraints such
as `$std::concepts::Option` are offered only in annotations and concept bases;
selecting a concept does not instantiate a module or construct a runtime Type.
LSP and REPL share the same prefix-aware type completion: `$` selects `$std::`,
then `concepts::`, then the final concept name. Edits replace only the active
segment, including when namespace separators contain whitespace. A lexical
`std` binding continues to shadow the predefined namespace.

Inline associated declarations use ordinary keyword coloring for `typename`
and type coloring for the declared name:

```msync
concept Product {
    first?: typename First,
    second?: typename Second,
};
concept Sequence {
    items: Range<typename Item>,
    accepts: fn(value: Item) -> Bool,
};
```

References navigate to the inline binder, including names introduced in nested
Range arguments. Completion offers `typename` in concept requirements, not in
ordinary function annotations. The old standalone spelling remains accepted
for existing source but is not suggested. Formatting preserves source-owned
declarations rather than synthesizing a duplicate type section; generic angle
brackets follow annotation spans and remain compact without changing `<` and
`>` expression operators.

Concept hover includes adjacent source documentation, required members and
bases. Standard concept references navigate into the running executable's
versioned `msync-stdlib` source, using the same bytes as compilation. Native
function inspection obtains parameter names and written constraints from those
declarations, while retaining the actual callable identity through aliases.

Named families such as `$std::containers::Set` and `$std::data::Option` use the
type role; their associated functions use the function role. Instances remain
ordinary anonymous Scope values. Function names use snake_case and class names
use PascalCase. Policy and instance member completion follows actual inferred
structure instead of guessing from names or adding implicit receivers.

Focused coverage lives in `tests/StandardConceptTooling` and
`tests/Msync.EndToEnd/standard_concepts_tooling_journey.py`. The latter uses an
actual server, persistent REPL and native terminal (ConPTY on Windows).
`MSYNC_TEST_STANDARD_CONCEPTS_ONLY=1` selects the independent VS Code host suite:
it edits a policy constructor call, follows embedded declarations, checks both
light and dark semantic roles, and checks/runs the resulting program with the
same frozen executable. The delivery report records the final Native image and
host run; managed development observations are not platform acceptance claims.
