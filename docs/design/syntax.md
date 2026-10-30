# Syntax and formatting

The parser is deliberately line-oriented: one declaration or copy per line, with comments introduced by `#` at line start or after ASCII whitespace. URI fragments and non-whitespace-delimited `#` in paths remain operand text. Double-quoted whole operands permit whitespace, literal operators and selector delimiters. Escapes decode quote, backslash, newline, carriage return and tab; unknown escapes retain their backslash to avoid silently damaging Windows paths or regexes.

A single byte scanner tracks quoted operands, regex selectors, escaped regex characters and regex character classes. Consequently `->`, `=>`, `:=`, `#` and `>` inside a selector character class cannot accidentally split a statement. An unescaped selector-closing `>` must be followed by whitespace, a path separator, an operator or end-of-line; this permits literal `>` within common regex alternatives. Escape an ambiguous literal greater-than sign. AST operands are decoded strings and statement spans are half-open original UTF-8 byte offsets. Parsing collects independent line errors. Diagnostic rendering clamps external spans to character boundaries, uses Unicode display widths for caret alignment, expands tabs to four-column stops and escapes terminal control characters. Human line:column coordinates count Unicode scalar values, independently of display width.

Formatting reuses the parser rather than maintaining another grammar. Comments become ordinary AST statements; inline comments canonicalize to the next line, retaining order and content. Empty lines are not retained. Quoting is chosen by the same scanner, and quoted backslashes are escaped. In manually quoted Windows paths, double backslashes to prevent recognized sequences such as `\n` from becoming control characters; bare paths never decode escapes. The key executable invariants are semantic AST preservation and formatter idempotence. Property tests cover arbitrary quoted Unicode operands and arbitrary input; fixed cases cover the supplied seed, CRLF spans, Windows paths and selectors containing operators. `parse` and `format` participate in the shared telemetry tree, marking errors without recording source text.

This follows the practical pattern in [rustfmt's contribution guide](https://github.com/rust-lang/rustfmt/blob/main/Contributing.md): reuse parsed syntax and test idempotence. The [Rust style guide](https://doc.rust-lang.org/style-guide/) motivates a canonical default rather than proliferating configuration knobs. Our much smaller language does not require a full concrete syntax tree because blank-line placement is not a semantic contract and every comment is retained explicitly.

## Operand provenance and structured diagnostics

`Statement.operands` records left/right operand sources and the actual operator token span. Bare operands map byte intervals directly; quoted operands retain a decoded-byte-to-original-byte map, so a semantic error in `"dir/<\\\\p{Unknown}>"` highlights the original escaped regex, not a guessed interval. Compiler diagnostics are constructed at the failing semantic check with explicit primary spans, secondary labels and help. No renderer guesses diagnostic meaning by matching English prose. Source metadata is optional for manually constructed ASTs; lowering still works, falling back to the statement range when precise provenance is unavailable.

`Report { diagnostic, labels, help }` is the richer envelope; `parse_rich`, `format_rich` and `Compiler::compile_rich` expose it. Existing basic `Diagnostic { message, span }` constructors and `parse`/`format`/`compile` return types remain compatible. `render_report(source, report, color)` colors only when requested. The CLI owns terminal/pipe/NO_COLOR policy. Same-line labels share one source snippet. Empty spans are real insertion points and render a single caret.

This design follows the [Rust compiler diagnostic guide](https://rustc-dev-guide.rust-lang.org/diagnostics.html): a standalone problem description, precise primary and secondary spans, and actionable suggestions. [Miette's diagnostic model](https://docs.rs/miette/latest/miette/) independently separates source labels from help text. The implementation uses this small model directly rather than introducing a rendering framework dependency for the current line-oriented language. Suggestions are advisory help rather than automatic edits: changing an endpoint or adding a destination slash can change copy semantics and requires the user's intent.

### v0.3.1: source-oriented presentation

`render_report_named(source, report, name, color)` adds a named location to the
same rich renderer, avoiding an outer error banner followed by a second error
heading. A report has one problem title, one primary `name:line:column` location,
source-ordered snippets, primary and secondary annotations, and explicit help.
`render_report` remains the unnamed entrypoint. The older `render_diagnostic`
keeps its compact single-line layout for callers that rely on that basic API.
`Diagnostic`, `Report`, source spans and the execution grammar are unchanged.

Rich snippets include one surrounding source line on either side of an
annotation. Labels on one line share the source row; primary carets are red and
secondary dashes cyan, so context is not confused with the failing token. A
large multiline external span shows its beginning/end windows with an explicit
ellipsis rather than flooding the terminal. Multiline annotations retain their
whole visible extent and attach their label at the end of the span.

Source rows use the same tolerant lexical highlighter as live input. Coloring
never changes displayed text: stripping its SGR sequences gives the exact plain
report. Source tabs expand to four-column stops before measuring annotations;
human locations count Unicode scalar values while carets use display width.
Malformed external byte spans clamp to valid UTF-8 boundaries, and EOF insertion
points remain visible even after a CRLF line ending. Gutter widths account for
all selected line numbers, including transitions to two or more digits.

Untrusted names, labels and problem titles escape terminal controls and bidi
formatting controls without dropping useful Unicode or path content. Help may
contain intentional multiline explanations, such as a regex parser detail:
continuations stay indented under their help field and other terminal controls
remain visible escapes. Paths and short cause information belong in the
appropriate producer fields; the renderer does not classify errors by matching
English prose.

`tests/syntax.rs` exercises named/unnamed compatibility, grouped labels,
multiline spans, bounded context, CRLF/EOF, wide characters, tabs, control/bidi
escaping and arbitrary malformed external spans. A property compares colored
reports with their plain equivalents after stripping SGR color codes. The
formatter's source-preservation and idempotence properties remain independent
of this display-only change.

## Contextual namespaces, physical endpoints and value aliases

`=>` binds a space, while `:=` binds a path value. Private `Space` distinguishes contextual `Pwd`/`File` namespaces from physical `Endpoint(Backend)` transports. This prevents the old accidental equivalence between a namespace and a directory. Public `Backend`, `Location` and `CopyOp` remain physical execution contracts and have no contextual namespace variants.

| Namespace access | Value | Permitted suffix |
| --- | --- | --- |
| `$pwd::pwd` | Absolute logical working-directory snapshot for this submission | Directory children, e.g. `$pwd::pwd/assets/` |
| `$file::this` | Absolute logical path of the named source script | None: this item is a file, not a directory |
| `$file::parent` | Parent directory of that source script | Directory children, e.g. `$file::parent/assets/` |

Namespace items resolve before any value-alias lookup. `$pwd::foo` is an unknown-item error, never an implicit cwd child, even if `foo` is an existing path alias. The intentionally revised syntax rejects old `$pwd/foo` and bare `$pwd`, pointing at `$pwd` with migration help to `$pwd::pwd/foo` and `$pwd::pwd`. Legacy ordinary unqualified relative paths retain their previous local-path behavior. The formatter remains syntax-only and does not guess namespace rewrites.

`this => $pwd` and `file => $file` bind namespace aliases without reading an item. Their items resolve against the context of each subsequent submission, including changes to cwd or the `/load` source script. `this := $pwd::pwd` and `script := $file::this` instead capture concrete absolute local paths at declaration. Value aliases transitively retain known-file provenance: `script/child` is rejected even after copying that value into another alias. Plain path-only aliases such as `root := ~/.codex` retain their existing endpoint-polymorphic behavior; binding a namespace item makes locality explicit.

`CompileContext { pwd, source_file }` is passed to `Compiler::compile_rich_with_context` or `compile_with_context`. `CompileContext::capture(Some(path))` captures absolute logical source identity without canonicalizing symlinks or probing filesystem metadata. Explicit contexts require absolute paths. The context-free compilation APIs capture the current directory and have no source file. CLI named-file intake supplies source context; stdin and direct REPL submissions do not invent one. Missing `$file` context points at the namespace qualifier and recommends running/loading a named script or using `$pwd::pwd`, rather than silently substituting cwd. Two scripts with the same basename in different directories therefore have different `this` and `parent` values.

Compiler state commits only after a complete successful compilation. A namespace alias can be declared even without its runtime source context (`file => $file` in a REPL); accessing `file::this` still requires a named-file context. Context capture is kept outside pure compile benchmarks so OS cwd lookup is measured separately from lowering.

These are explicit binding-time rules, not filesystem-resolution heuristics: namespace aliases preserve a lookup capability, value aliases preserve its resolved result, and the execution plan sees only physical locations. No filesystem canonicalization, source reading or remote probing is introduced into semantic compilation.

## Stream endpoint syntax

TCP endpoint declarations use `tcp://host:port` with mandatory explicit port, no credentials/query/fragment and no nontrivial URL path. They lower to `Backend::Tcp`; execution and stream framing are owned by the plan/stream layers, not encoded as parser special cases.

## 0.5 item-expansion diagnostics and operator contracts

The current operator contract is explicit in
[the language reference](language-v4.md#-operator-evaluated-item-composition):
`/` evaluates item/string operands, including `:=` expansion on the right. It
has **no space overload**. `$pwd` is a space; `$pwd::pwd` selects its cwd item;
`pwd => $pwd` merely creates another space name whose item is `pwd::pwd`.
Space-related home access remains `~`, e.g. `remote::~`. The earlier implicit
space-to-path branches and source generators using them are corrected, not
replaced with an atomic-path parser. Item and literal composition remains.

A rooted RHS can legitimately appear only after expansion. The compiler retains
its original semantic error at `/`, but labels the actual expanded LHS/RHS and
related item definitions rather than presenting an unexplained "rooted" label.
For `.codex := ~/.codex`, `remote::~/.codex/config.toml` shows that the right
item `.codex` expands to `~/.codex`; it does not reinterpret the user's item as
unexpanded filename text or silently discard the left root.

`compiler/structured/diagnostics.rs` owns private, bounded expansion traces.
Each declaration records its own source identity, a bounded typed value summary,
and shared references to relevant inherited frames. Macro parameter traces use
argument sites; explicit globals keep definition-time snapshots. Rebinding a
name cannot rewrite earlier traces. Storage as well as rendering is limited to
16 flat frames, avoiding both unbounded REPL chains and recursive graph drops.
Displayed values/names retain at most 160 Unicode scalars with explicit elision.
The normal successful resolver does not build an error report or re-run IO.

`Scope.sites` still describes semantic operand origin and may intentionally
collapse aliases. It is **not** repurposed as a declaration site. New objects
export known own declaration locations using the existing debug-site schema;
imported frames are explicitly labeled `source origin`, so older objects with
collapsed source origins do not falsely claim to retain an alias declaration or
a complete expansion chain. No public `Report` constructor or executable IR
format is changed. `LocatedReport` already owns cross-source labels, including
separate REPL submissions with the same display name.

The diagnostic visitor must follow actual namespace lookup order: unknown or
non-space qualifiers fail before RHS lookup, `$file::parent`/`this` do not expand
same-named user bindings, and intrinsic `$pwd::pwd` does not consult a user
`pwd` item. Negative provenance tests protect these distinctions; a plausible
but unvisited binding is worse than an omitted frame.

This follows production compiler practice rather than inventing a diagnostic
language: [Clang bounds template-instantiation notes](https://clang.llvm.org/docs/UsersManual.html#options-to-control-error-and-warning-messages),
[GCC exposes template structure](https://gcc.gnu.org/onlinedocs/gcc/Diagnostic-Message-Formatting-Options.html#index-fdiagnostics-show-template-tree),
and [Rust keeps call-site expansion ancestry](https://rustc-dev-guide.rust-lang.org/macro-expansion.html#the-call-site-hierarchy).
Here those mechanisms map to item definitions, typed expanded operands and
macro argument/call sites, not arbitrary host environment dumps or remote HOME
probes. Local Clang/GCC template-error probes and the real msync REPL/copy/IR
journeys were run during implementation; disposable evidence is under
`.temp/syntax-restoration/`.

Relevant regressions are `tests/source_v4.rs`, `tests/join_v4.rs` and
`tests/cli.rs`: real error/recovery flows, cross-source alias and macro snapshots,
old/new imported origins, rejected space operands, explicit cwd-item copying,
bounded chains, Unicode/color properties, and replayable binding serialization
without generated space `/` expressions.

Recent research on [Gradual Metaprogramming (2025)](https://arxiv.org/abs/2506.09043)
also treats identifying the generating source location as a first-class problem.
The applicable lesson here is to preserve the binding/argument responsible for
an invalid expanded value, not merely report the final failed operation. Its
gradual-type calculus is not needed for this already typed, compile-time resolver.
