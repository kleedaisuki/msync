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
