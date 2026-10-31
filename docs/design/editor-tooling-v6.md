# Version 0.6 editor tooling

## Ownership and invariants

The native REPL, diagnostics and examples share `highlight::token_spans` and
`highlight::highlight`. Tokens remain half-open **UTF-8 byte spans** covering the
entire original source without overlaps or gaps, including incomplete edits.
`TokenKind`, `COMMANDS` and `BUILTINS` retain their established public roles.
`KEYWORDS` and fully qualified `RANGE_ADAPTORS` now provide one static vocabulary
for completion and language-server tooling; they do not perform IO or evaluate
user code. Declaration parameters and loop binders reuse the `Binding` role;
range namespace prefixes retain `Namespace` and adaptor names reuse `Binding`.

The renderer is tolerant lexical tooling, not another acceptance grammar or a
compiler invocation per key. Quotes, regex character classes and escapes shield
operators and comments. Compact string comparisons do not use a search for a
later `>` to guess regex selectors. Shell shortcut payloads remain plain text.
Control-character escaping and ANSI safety are unchanged. Native VS Code
semantic tokens use its UTF-16 protocol coordinates rather than exposing Rust
byte offsets to the editor.

## Rendering implementation

- Lines append into the single document token vector rather than allocating and
  extending a temporary vector per physical line.
- A small statement-head state recognizes scheduling prefixes without repeatedly
  splitting every preceding word; parameter/binder state is local to one line.
- Operators use longest two-byte ASCII matching before single-byte tokens.
- Plain rendering bypasses tokenization entirely but still passes through the
  same control-character escaping routine.
- Colored rendering appends SGR fragments directly instead of allocating a
  `format!` result for every token. Visible safe runs are copied as slices;
  only actual controls require escape expansion.

These changes reduce allocation and scanning in the real redraw path without
introducing a cache, stale lexical state, or an invalidation protocol.

## Native VS Code integration

`editors/vscode` is a real extension package, not instructions to manually color
files. It contributes `.msync`, `source.msync`, a TextMate grammar, language
configuration and snippets. The grammar loads through the editor's native
Oniguruma engine before the language server starts and stays useful when the
server executable is unavailable. Standard scopes cover declarations/functions,
parameters, booleans, operators, ranges, namespaces, paths, strings, escapes,
selectors and comments. Single-line string/selector rules terminate at EOL,
matching the source grammar and avoiding an unfinished edit poisoning the file.

The extension uses Microsoft's package-managed `vscode-languageclient` 11.0.0,
starts the configured `msync.serverPath` executable directly with
`--language-server`, and selects file and untitled msync documents. The executable
setting is machine-scoped and no shell is invoked. Activation failures retain
native highlighting and show the cause plus repair guidance. Deactivation waits
for client shutdown. The client negotiates semantic tokens and other LSP features
with the Rust server; it does not implement a second compiler in JavaScript.

The supported minimum host follows the client package: VS Code 1.91. Runtime
libraries are bundled with mature `esbuild`, with third-party license texts
collected from the installed lockfile's runtime packages. Native grammar assets
remain separate. The initial unbundled VSIX had 392 files and was 694 KiB;
bundling reduces that to 10 files and 118.51 KiB, removing per-module loading
overhead rather than adding custom module loading machinery. Packaging is
exercised with the official `@vscode/vsce`.

Official references guiding the choices:

- [VS Code syntax highlighting](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide)
  explains TextMate scopes, native Oniguruma grammars and the scope inspector.
- [VS Code language configuration](https://code.visualstudio.com/api/language-extensions/language-configuration-guide)
  supplies comments, pairs, indentation rules and indentation-based folding.
- [VS Code LSP extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide)
  separates the editor client from the reusable server.
- [Microsoft vscode-textmate](https://github.com/microsoft/vscode-textmate)
  exposes the same line/token state API used by our actual-engine grammar tests.
- [VS Code bundling guidance](https://code.visualstudio.com/api/working-with-extensions/bundling-extension)
  recommends consolidating many dependency files and documents esbuild.

The latest stable client still inherits the moderate minimatch advisory
[GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249) through
`minimatch ^10.2.6`. The published fix is 11.0.1. We do not force an incompatible
major dependency override. Our document selectors contain no glob patterns and
the configured server is trusted; revisit the audit when upstream updates.

## Focused evidence

The actual-source isolated harness includes the real language, syntax and
highlight modules with only a no-op telemetry stub, so editor tests need not
wait for concurrent runtime/compiler work. It lives under `.temp` and is not a
second production implementation. `tests/highlight_v6.rs` covers declaration
parameters, loop binders, every range spelling, compact comparisons, regex
shielding, incomplete input, CR-only lines and UTF-8 coverage. Existing
`tests/highlight.rs` (3), `tests/language_presentation_v6.rs` (4), and all eight
current highlighter unit/property cases pass. Baseline-copy property cases also
pass in the same harness.

`npm test --prefix editors/vscode` passes eleven tests: actual TextMate/Oniguruma
scopes and byte-shielding behavior, every shared range spelling, incomplete
editing, configuration consistency and mocked activation/failure/shutdown.
Mocked activation is not evidence of an actual GUI session; the separate native
gate below closes that integration gap. Rust stdio tests remain useful focused
transport checks, rather than substitutes for the actual editor client.

### Real Extension Development Host gate

The official `@vscode/test-electron` 3.1.0 runner is pinned as a dev dependency;
its published version and Node >=22 requirement were verified against both
Microsoft's source and the npm registry. `test/integration/run.cjs` launches
the production manifest's `out/extension.cjs` in a real managed VS Code process,
not a mocked VS Code API. It sets HOME/XDG/TMP before loading the runner, isolates
user data, extensions and workspace under project `.temp`, selects X11 under
Xvfb, and clears inherited Electron/VS Code launcher hooks. The downloaded
editor lives in the package-managed `.vscode-test` cache, explicitly excluded
from Git and VSIX packaging.

The first actual host run passed on 2026-10-31 with VS Code 1.144.0, packaged
product commit `2c33b50b16c472efe58357d1d2a8a5414bc23fbf` dated
2026-10-27T20:18:03Z, host Node 24.22.1 and runner Node 26.13.0. The official
download's `x-source-commit` header was separately
`b224f6bf5a4fcad5fa4c640ed10f179c3b613e48`; this differs from the packaged
runtime product commit and is not mislabeled as that build identity. The
Electron executable SHA-256 was
`d1c6fe7cec1e285f1210cd3c0472099c25bb56e94f0c8ef7384ee3229ed0b518`.
The frozen server reported `msync 0.6.0`, with binary SHA-256
`7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`;
the production bundle SHA-256 was
`33cd1ffbd339f1c4a0ebc9fb8e10da996584c257040160c4e6778b2949089fa2`.

| Real-host phase | Observed result |
| --- | --- |
| Production activation | `msync.msync-language` active in the actual host |
| Typed diagnostics | String-valued `if` rejected with the real bool-type diagnostic |
| Incremental edit | Replacing the invalid condition cleared diagnostics |
| Document formatting | 14 edits applied through VS Code's formatting command |
| Range completion | `views::filter` returned through the real client |
| Semantic tokens | 29 tokens; exact `"可莉🌟"` string, function and parameter roles; valid UTF-16 ranges |
| No DSL execution | Both absolute transfer outputs absent; absolute delete victim unchanged |

The source fixture deliberately contains transfer and delete statements, with
explicit `$local::` absolute paths so a wrong process cwd cannot conceal effects.
Each provider call has a bounded timeout, and failures retain active-extension,
configuration and diagnostic details. The run's `run.json`, `result.json`,
`host-results.json` and `host.log` are under `.temp/vscode-host-jQ5Ato`; the table
above preserves the useful evidence when temporary logs are cleaned. This is
real automated headless editor integration, not human visual inspection.

Reproduce after `npm ci --ignore-scripts` and `npm run build` in
`editors/vscode`:

```sh
MSYNC_TEST_SERVER_PATH=/absolute/path/to/frozen/msync xvfb-run -a npm run test:integration
```

The server path is required and hashed, editor versions can be pinned through
`MSYNC_TEST_VSCODE_VERSION`, and an existing managed editor can be selected using
`MSYNC_TEST_VSCODE_EXECUTABLE`. The runner API follows Microsoft's
[official extension-testing guide](https://code.visualstudio.com/api/working-with-extensions/testing-extension).

The final source-aligned VSIX at `dist/editor/msync-language-0.6.0.vsix` was
packaged with `@vscode/vsce` 4.1.0; archive inspection confirms the bundled
entrypoint, grammar, language configuration, snippets, GPL license and all
runtime third-party license texts are included. Its SHA-256 is
`66271147afb0842b8f97deb7c31ecf9edbf0fb8cd23483820295905182a09adc`.
The archived entrypoint matches the real-host-tested bundle SHA-256 exactly.
Archive listing confirms no cached `.vscode-test` editor, integration-test
source or `node_modules` tree was included, despite the managed editor cache
being present during packaging.
The final npm audit reports two moderate findings (the upstream minimatch issue
and its client dependency edge), with no high or critical findings.

### Same-process release rendering comparison

On 2026-10-31, the release harness compiled the frozen pre-optimization
highlighter and the optimized snapshot identified below into the same binary and compared
`highlight(source, color)`. Each corpus received warmups, then seven timed
batches with alternating baseline/current order; the table reports median
nanoseconds per call. `std::hint::black_box` retains input/output work. Corpus
sizes are 50 bytes for a live editing line, 332 bytes for a 0.6
function/control/range script, and 42,496 bytes for that script repeated 128
times. Batches use 100,000 / 20,000 / 200 calls respectively.

Host: Linux x86-64, QEMU Virtual CPU 2.5+, 16 visible vCPUs; rustc 1.99.1
(2026-10-13), release optimized. No CPU pinning or exclusive host reservation.
The isolated harness has no enabled telemetry overhead. These are rendering
measurements, not claims about terminal display latency or end-to-end sync time.

| Corpus | Color | Before ns | After ns | After / before |
| --- | --- | ---: | ---: | ---: |
| Live line | no | 2,509 | 188 | 0.075 |
| Live line | yes | 3,608 | 2,271 | 0.629 |
| 0.6 script | no | 14,559 | 1,056 | 0.073 |
| 0.6 script | yes | 20,608 | 11,177 | 0.542 |
| 128× script | no | 1,857,863 | 128,694 | 0.069 |
| 128× script | yes | 2,543,194 | 1,341,464 | 0.527 |

The large plain-output improvement is explained by removing unnecessary
lexing, not by faster token classification. Colored output gains come from
one token buffer, fewer allocations and slice copying. Both versions retain
identical visible text; additional 0.6 token classifications may produce
different SGR segment boundaries.

Frozen baseline SHA-256:
`0da12688ff3bde2f0af00cd9688efb259848ffb4e5a122eedc4215bed08c0197`.
Measured optimized snapshot SHA-256:
`37382765d0e3f190adeabe6bae9cbad9e29f267bc6ea1c880c0b5eda5063014d`.
This rendering table predates the shared LF/CRLF/CR iterator correction. It is
not a new timing claim for the later source or the real-host-tested artifact.
The local runner and raw CSV are
`.temp/frontend-v6-harness/highlight-bench.rs` and `highlight-bench.csv`;
rerun with `cargo run --release --manifest-path
.temp/frontend-v6-harness/Cargo.toml --bin highlight-bench` while those snapshots
are retained. The durable result is the mechanism and table above, not a claim
that ignored temporary files are permanent project artifacts.
