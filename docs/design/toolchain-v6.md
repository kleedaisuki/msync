# Native editor and language-server tooling

## One source language, two display layers

`msync --language-server` is a stdio Language Server Protocol (LSP) endpoint.
The executable dispatches this mode before ordinary CLI configuration/telemetry
so an invalid user configuration or terminal banner cannot corrupt JSON-RPC.
All stdout bytes are framed protocol messages; operational failure belongs on
stderr, and malformed notifications produce structured `window/logMessage`
context rather than silently disappearing.

The server does not implement an alternate DSL grammar. Diagnostics use the real
`language::parse_recovering`, version-header selector and
`compiler::structured::Session::compile_named`; formatting uses the canonical
language writer. Navigation uses the AST-owned
[lexical index](tooling-index-v6.md). The existing tolerant highlighter supplies
fallback display tokens for incomplete edits, and AST definitions/references
supply semantic function/namespace/parameter/variable classifications.

The compiler may read requested `include`/`execute` source files as compiler
inputs, under its existing limits. It never runs DSL transfers, deletion,
existence queries or external programs. Referenced source files currently use
the compiler's disk provider rather than editor overlays; navigation/completion
are document-local, and do not claim project-wide included-file symbol lookup.
Compiler diagnostics retain owned cross-file locations as related information,
including UTF-16 coordinates computed from the actual child source.

## Transport, document state and latency

The mature `lsp-server` crate owns JSON-RPC framing/initialization/shutdown;
`lsp-types` owns protocol data structures. `line-index` supplies established
UTF-8/UTF-16 mapping rather than a separate coordinate algorithm. The server
advertises UTF-16, incrementally synchronized text, completion, hover, definition,
hierarchical document symbols, formatting, and full semantic tokens.

Each open document owns original source bytes, the line index, a monotonically
fresh internal generation, the client version and a lazily cached recovering
AST/index. Invalid edits cannot partially mutate the document. Stale client
versions are ignored; closing releases text/index state and clears diagnostics.
Source offsets remain UTF-8 byte spans internally. Astral characters are two
UTF-16 code units, CRLF is one line ending, and LSP lone-CR coordinates are
supported without rewriting original source bytes. Positions beyond a line end
clamp to that end; edits inside a surrogate pair are rejected with context.

Typing invalidates the document cache, not every downstream request separately.
The receive loop coalesces changes for a 60 ms idle window. One background
compiler worker owns type-expansion diagnostics, with only the newest queued
job retained per URI. Navigation requests do not wait behind compiler expansion.
Completed results are published only for the same internal generation; an old
result cannot leak into a reopened URI merely because the client reused version
1. Queued document messages are handled before completed diagnostics. Shutdown
does not wait for a currently running pure compiler job. Source analysis is
bounded to 8 MiB per document, matching the existing source-file limit.

The synchronous providers have no independently queued work to cancel; late
`$/cancelRequest` notifications are harmless. This is not an incremental typed
compiler or a promise of interrupting a current compiler expansion. The explicit
worker/state boundary allows adding cooperative compiler cancellation later
without changing the LSP contract.

## Editor installation

The package under `editors/vscode` contributes the `msync` language and `.msync`
file association, a native TextMate grammar, comment/bracket/indentation rules,
snippets, and a `vscode-languageclient` stdio client. Its native grammar works
immediately, including while the language server starts or is unavailable;
semantic tokens enrich that baseline rather than replacing it.

Install the packaged VSIX using VS Code's **Extensions: Install from VSIX**
command, or build it using the package's documented locked npm workflow. Install
`msync` on PATH or set `msync.serverPath` to the executable's absolute path.
Open a `.msync` file; the client starts `msync --language-server`. The selector
covers local files and untitled msync documents. Server startup errors point to
the configured executable/output channel while native syntax coloring remains
available. Only configure trusted server executables: that setting deliberately
launches a native process, just like other language-server clients.

Other LSP editors can launch the same executable/argument pair over stdio and
associate `.msync` with their `msync` filetype. They should not run the normal
interactive REPL as the language server.

## Prior art and implementation choices

- The [LSP specification](https://microsoft.github.io/language-server-protocol/)
  separates editor capabilities from transport and defines UTF-16 positions,
  incremental document changes, diagnostics and semantic token deltas.
- [`lsp-server`](https://rust-lang.github.io/rust-analyzer/lsp_server/index.html)
  is the rust-analyzer synchronous protocol scaffold. Keeping one dispatch loop
  and one bounded/coalesced compiler worker avoids a second async runtime or
  per-keystroke process spawning.
- [VS Code syntax highlighting](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide)
  explicitly layers semantic information over native TextMate tokenization.
  [The language-server extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide)
  recommends the maintained language-client library instead of custom editor
  protocol code.

The grammar package uses the real TextMate/Oniguruma engine for tokenization
fixtures, not JSON-schema validity alone. Tests exercise the native fallback
and client activation/failure orchestration independently of Rust transport.
Dependencies and any unresolved upstream audit findings are documented with the
package rather than hidden with an incompatible transitive-major override.

## Focused verification ownership

- `tests/tooling_v6.rs`: lexical snapshots, distinct callable/value namespaces,
  explicit globals, parameters, drop/assignment lifetimes, intrinsic namespace
  members, UTF-8 spans and final-scope bounds.
- `tests/language_v6.rs`: recovering AST preserves valid statements and actual
  error spans instead of returning stale AST coordinates.
- `language_server::documents` unit tests: UTF-16 round trips, astral boundaries,
  CRLF/lone-CR positions, sequential atomic incremental edits and stale versions.
- `language_server::providers` unit tests: completion under incomplete edits,
  protected lexical regions, reference-local hover ranges, definition locations,
  nonoverlapping semantic token deltas and astral string lengths.
- `language_server::analysis` unit test: include-site primary diagnostics with
  original child-URI UTF-16 related locations.
- `tests/performance_lsp_journeys.rs`: independent actual-process stdio handshake,
  Unicode diagnostics, incremental correction, semantic tokens, completion,
  navigation, formatting, document close and clean shutdown.

Execution results belong in the root's acceptance record after the relevant
Cargo window, not in an optimistic list of authored tests. The lexical index's
9 tests have already passed in the isolated actual-source frontend harness;
full executable acceptance is owned by the integration gate.

The root-owned production-crate gate
`.temp/performance-v6/storage-tooling.log` passed all 11 lexical-index tests,
including the final range-builtin/callback navigation fixes and loop-body
completion visibility. This supersedes the earlier 9-test isolated-index result.

The shared `syntax::physical_lines` iterator now supplies LF, CRLF and lone-CR
boundaries to the structured parser, header selector, tolerant scanner and CLI
source renderer without double-counting CRLF. Source-owned related locations use
the same renderer. CLI location columns retain the established Unicode-scalar
count, while caret indentation uses terminal display width and LSP positions use
UTF-16; these are intentionally different coordinate contracts, not interchangeable
integers. `tests/source_lines_v6.rs` exercises exact real-CLI line/column output
and primary/related labels for all three newline encodings with Unicode input.

The root's focused production-crate gates subsequently passed all 6 initial
language-server unit cases and all 5 native editor unit cases
(`.temp/performance-v6/unit-gates.log`). Both real stdio journeys passed across
the incremental gates: child-source provenance in `features-remaining.log`, and
Unicode/edit/navigation/format/shutdown in `final-functional.log`. The latter
initially exposed an outdated test expectation omitting the writer's intentional
binary-expression parentheses; the expected canonical string was corrected,
then only that failed journey was rerun. Both `source_lines_v6` cases also passed
in `final-functional.log`, including real CLI execution for LF/CRLF/CR.

A final narrow recovery regression was then added: a partial first operand in a
new macro body must still offer its known Unicode parameter, retain explicit
`global` visibility and avoid leaking parameters after dedent. Only recovering
compound spans with actual child parse errors are extended through consumed
indented source; invalid code remains rejected by `parse()`, and successful AST
ranges are unchanged. The root's `freeze-gates.log` passed that one new filtered
provider regression; it was not assumed to be covered by the earlier 6-test run.

## Real native extension-host acceptance

The additional native gate runs the production bundled extension inside a real
VS Code Extension Development Host under Xvfb using Microsoft's pinned
`@vscode/test-electron` 3.1.0. This closes the earlier mocked-activation boundary;
it is not a substitute mock or a claim of human visual inspection.

The gate passed on 2026-10-31 with VS Code 1.144.0, packaged product commit
`2c33b50b16c472efe58357d1d2a8a5414bc23fbf`, and the preserved optimized msync
0.6.0 server SHA-256
`7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`.
Its seven phases verified real activation, a typed bool diagnostic, diagnostic
clearing after an edit, 14 formatting edits, `views::filter` completion, and 29
semantic tokens including an exact astral-containing Unicode string and
function/parameter roles. Both absolute transfer destinations remained absent;
the absolute deletion victim retained its bytes. Editor profiles, HOME, temporary
state, extensions and the test workspace were isolated below project `.temp`.
The official runner manages its downloaded editor in ignored `.vscode-test`.

The final source-aligned `dist/editor/msync-language-0.6.0.vsix` contains 10 files
and 121,353 bytes, SHA-256
`66271147afb0842b8f97deb7c31ecf9edbf0fb8cd23483820295905182a09adc`.
Its archived entrypoint hash exactly matches the real-host-tested bundle;
archive inspection excludes the downloaded editor, tests and `node_modules`
while retaining all runtime license texts. The [editor evidence note](editor-tooling-v6.md)
records the complete environment, the distinct CDN header/runtime build IDs,
reproduction commands and dependency audit. Reusable host code is
`editors/vscode/test/integration/{run,suite}.cjs`; it performs no Cargo build or
server mutation. This verifies the toolchain against the explicitly recorded
server snapshot, independently of later engine-performance experiments.

## Focused editor maintenance CI

`.github/workflows/editor.yml` maintains the native toolchain independently of
the Rust test-selection matrix. Pushes and pull requests trigger it only for
the editor package, LSP/tooling/highlight/parser and typed-compiler integration
sources, Cargo/build inputs, or the workflow itself; manual dispatch is also
available. Unrelated runtime or documentation-only edits do not trigger this job.

The Linux job selects Node 26, installs the locked npm tree with scripts disabled,
runs the actual TextMate/client tests, builds the production bundle, and builds
only `cargo build --locked --release --bin msync` rather than running the Rust
suite. The real-host command uses the absolute freshly built server path and
pins VS Code 1.144.0, matching the locally passed host gate. It then packages the
VSIX using pinned `@vscode/vsce` 4.1.0 and retains the installable archive plus
the runner's focused JSON/log evidence for 14 days. No editor cache, profile or
workspace is uploaded. The runner itself continues to isolate those below
project `.temp`.

The workflow has read-only repository permissions, checkout credentials disabled,
cancel-on-newer-run concurrency, a 25-minute job bound, and bounded build/host
steps. Its shell command bodies and YAML structure were checked locally with
Bash and PyYAML, including trigger-path existence and the pinned host/server
settings. This is validation of the CI definition, not a claim that GitHub
Actions has executed successfully; the underlying npm/build/Xvfb/packaging
commands were exercised locally as recorded above.
