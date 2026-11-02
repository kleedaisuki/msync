# AST-backed lexical tooling index

## Ownership and boundary

`src/tooling.rs` builds `SymbolIndex` from the same owned `language::Document`
that the compiler consumes. It does not tokenize an independent dialect, evaluate
functions, inspect paths or run the planner. The language-server document cache
owns one index per parsed document version. Definition identities are valid only
within that index; callers must never preserve them across edits.

The index provides declaration/reference byte spans, visible declarations at a
byte offset, declaration lookup under a cursor and syntactic signature details.
LSP UTF-16 conversion, diagnostics, transport and editor-specific presentation
remain outside this module. Signature details describe known syntax, not claimed
compiler inference for generic functions. Pure function calls returning spaces
are not mislabeled as native processes merely because both use `=>`.

## Lexical model

The compiler has separate value, macro and pure-function tables. The index keeps
those separate too: a path reference consults values, a call consults pure
functions first and then macros in statement-call position, and a program member
consults its qualifier without interpreting `stdout` as an unrelated value.
Completion can consequently expose same-spelled callable and value declarations
with distinct kinds rather than silently discarding one.

Each scope records its initial visible declarations plus ordered install/drop
events. Completion replays events up to the cursor, so names are not suggested
before declaration or after `drop`. `=` on an existing local records an assignment
reference to the original declaration; `:=` introduces a fresh declaration. This
keeps navigation stable during ordinary bool/string state updates.

Templates capture snapshots before installing their own declaration:

- Macros inherit earlier macro and pure-function definitions, but not scalar
  values implicitly. Symbols and booleans inherit earlier pure functions only.
- `global` links to the definition-time captured value, or captured callable when
  no captured value has that spelling. Local conflicts do not overwrite the
  conflicting local in the index; compiler diagnostics explain the invalid code.
- Nested value captures merge ancestor captures with the parent's current locals.
  This supports explicit transitive imports without dynamic caller lookup.
- Branches and loops clone lexical visibility. Fresh declarations, dropped names
  and iterator bindings remain within that block. Assignments retain the identity
  of existing outer values.
- Dropping an outer spelling ends its visibility but does not rewrite references
  already captured by an earlier template.

Scopes remain byte intervals, and the root scope extends beyond the last valid
statement for incomplete-document completion. A final nested scope is bounded by
its AST rather than extending forever with the root. The parser's recovering
entry point allows valid statements in an incomplete document to be indexed
without keeping stale spans from the previous document version.

## Paths and intrinsic members

Quoted data is never a lexical reference. Unquoted path components can refer to
known values, including typed namespace qualification. The index propagates only
simple syntactically established namespace shapes: a native program, `$pwd` or
`$file`, including direct aliases. Intrinsic members such as `$pwd::pwd` and
`module::this` must not navigate to same-spelled ordinary bindings. Likewise,
program `stdout` is a member, not a value reference.

This is intentionally source navigation rather than a second compiler. Generic
function return types are not evaluated by the index. Cross-file navigation into
`include`/`execute` sources is not implemented: source expansion and associated
diagnostics remain compiler-owned. The server should not advertise workspace-wide
reference or rename support on the basis of this document-local index.

## Verification

`tests/tooling_v6.rs` contains focused tests covering:

1. Definition-time and transitive `global` snapshots.
2. Explicit value imports versus implicitly visible earlier callables.
3. Assignment identity, `drop` lifetimes and retained captures.
4. Branch, iterator and value-loop locality.
5. Separate callable/value namespaces, program ports and quoted paths.
6. Owned Unicode spans, signatures and nested-template parents.
7. Intrinsic namespace members and simple aliases under lexical shadowing.
8. Pure space-returning calls distinguished from process launches.
9. Bounded final nested scopes with persistent root visibility.
10. Reserved range namespaces/adaptor names versus lexical callback/count/seed arguments.
11. Ordinary same-spelled user functions outside range application.

Range call/index traversal follows AST context: `views`/`ranges` callee namespaces
and bare pipeline adaptor names are reserved by the compiler and never become
references to unrelated lexical values. Callback positions resolve pure-function
definitions, while counts, seeds and search values retain normal value lookup.
This does not turn all same-spelled user functions into standard-library calls;
ordinary calls outside pipelines keep the compiler's lexical meaning.

## v0.6.5 typed-expression integration

The index traverses each item of the owned `ExprKind::Range` node in order, using
the same lexical environment and call rules as an ordinary expression. Bool
items do not introduce declarations, and symbol/boolean calls inside a list
resolve to their original definitions. A scope-qualified pure call keeps its
lexical function identity; applying a scope to its return value does not turn the
function name into a filename or native process in navigation.

`$view::` is the canonical built-in API namespace for both views and scalar
consumers. The legacy `views::`/`ranges::` spellings remain reserved aliases.
Qualified applications may have either `Call(qualified callee, arguments)` or
`Select(scope, Call(member, arguments))` AST structure. One source-index call
normalizer handles both before traversing arguments: an unrelated lexical value
named `filter` must never capture `$view::filter`, while a predicate argument must
still resolve to its lexical boolean/symbol declaration. Scheduled conditionals
are existing `Scheduled(If)` nodes, so the established branch-scope rules apply
without inventing another visibility model.

Direct `$view` aliases reuse the index's existing syntactic namespace-shape
tracking, including a later direct alias of that binding. The namespace token
links to its declaration; intrinsic API members do not link to unrelated values.
Completion uses that same fact and replaces only the member after `alias::`.
This does not interpret generic functions returning Scope or treat physical
nested scope composition as a lexical module import.

The new compiler's inferred operator capabilities remain authoritative for
acceptance and diagnostics. The source-only index does not separately infer a
generic function's return type, resolve remote homes or evaluate range callbacks.
This follows the syntax/semantic-model separation described in the
[rust-analyzer architecture](https://rust-analyzer.github.io/book/contributing/architecture.html):
incomplete syntax stays useful to editor requests without pretending that a
syntactic hint is a checked semantic type.

The initial nine passed in the actual-source frontend harness under
`.temp/frontend-v6-harness` using `cargo test --manifest-path
.temp/frontend-v6-harness/Cargo.toml --test tooling_v6`. The harness imports the
real production modules by path and only substitutes no-op telemetry; it does not
substitute the language parser or index. `cargo clippy --manifest-path
.temp/frontend-v6-harness/Cargo.toml --lib -- -D clippy::all -A unused-imports
-A dead-code` passed; the two suppressed warnings are artifacts of including only
a frontend subset, not index lint exceptions. End-to-end protocol behavior is
verified separately by the language-server acceptance target.

The range-reference regressions and the value-loop visibility assertion were added
after source-only integration review. They await the root-owned Cargo window;
source presence is not reported as passing execution evidence.

## v0.7 namespace and embedded-source navigation

Explicit `scope` declarations own a member table in the source index. Their
ordinary declarations retain individual definition identities and use the scope
as their document-symbol parent. Qualification resolves against that member
table, including namespace aliases and nested namespaces, instead of accidentally
capturing a same-spelled caller binding. Call arguments remain in the caller's
lexical environment. Native declarations are callable source symbols, with
parameter names and return types retained in their signatures.

`tooling::standard_library()` lazily indexes the actual embedded
`stdlib::SOURCES` once. The exported inventory comes from each source module's
immediate declarations, not a second filesystem API name list. Source-only
namespace shapes track `$std`, its modules, and direct aliases; the index does
not execute user functions to guess a returned namespace. Filesystem, string,
path and Boolean completions, hover, signature help and definitions share this
source inventory. Existing intrinsic range adaptors retain their shared lexical
inventory and established `$view` completion forms while the canonical form is
`$std::ranges`.

The server returns versioned `msync-stdlib:///<package-version>/<module>.msync`
locations. `msync/stdlibSource`, with `{ "uri": "..." }` parameters, returns the
embedded source for exactly those module URIs. VS Code registers a read-only
`TextDocumentContentProvider` for this scheme after the production language
client starts. Thus installed and relocatable executables navigate into their
own standard library without repository paths, mutable extraction caches, or
requiring write access to the executable's installation directory. Source files
are also packaged separately for direct inspection.

This uses VS Code's official
[virtual document provider](https://code.visualstudio.com/api/extension-guides/virtual-documents)
contract. Keeping the cached syntactic index separate from execution and compiler
acceptance follows the production separation described in the
[rust-analyzer architecture](https://rust-analyzer.github.io/book/contributing/architecture.html).
The relevant design observation is that navigation is an identity relation over
source declarations, not a partial reimplementation of runtime type inference.

Focused acceptance targets are `tests/tooling_v070.rs` (namespace and alias
identities and actual library source spans), provider tests, `tests/lsp_v070.rs`
(real stdio protocol), and the installed Extension Development Host journey
(native source opening through the production provider). Execution results are
recorded after the integrated compiler/runtime image is available.

Dependent range primitives are actual `Native` AST declarations. Their result
arrow is omitted because the native signature infers the result from the input
range and specialized callback, for example
`native filter(values, predicate) = "ranges.filter"`. The same source index
handles these and concrete-return native declarations; there is no comment
scanner, fabricated declaration identity, or separate navigation inventory.

The first real v0.7 host run exposed an interoperability distinction hidden by a
raw stdio test: VS Code normalizes an empty-authority virtual URI from
`msync-stdlib:///version/module.msync` to `msync-stdlib:/version/module.msync`.
Embedded-source lookup therefore resolves the URI's scheme and versioned path,
not its serialized spelling. Both forms identify the same source bytes. The
provider and actual-process regression retain the editor-normalized spelling.

### Integrated v0.7 acceptance

The production crate passed 20 source-index test functions across the focused
`tooling_v070` (4), `tooling_v065` (5), and `tooling_v6` (11) targets. The latter
was rerun alone after repairing the legacy reserved-namespace shadow case; the
accepted new namespace/source cases were not swept into a blanket regression.
`lsp_v070` passed its real child-process journey, including retrieval using VS
Code's normalized virtual URI spelling. Seven production provider functions
cover completion, signatures, source/member definitions, UTF-16 edits/tokens,
and retained lexical behavior. Lexical/REPL evidence is recorded in the editor
note rather than duplicated here.

The real VS Code **1.144.0** Extension Development Host run started at
`2026-11-02T15:16:44.240Z` and passed all **12 phases**, using the production
esbuild bundle and frozen msync 0.7 executable SHA-256
`10882813ca187029b494db2bf1d4841e69aadee175dc333f17921913d1c6f9d2`.
It opened the actual `fs.msync` native declaration and `bool.msync` ordinary
function source, then resolved a parameter reference inside that virtual source
document through the real language client. Namespace member navigation and API
alias completion passed; formatting returned 20 edits and semantic tokens
contained 87 valid entries including the Unicode string/function/parameter
roles. Both transfer outputs remained absent and the delete victim stayed
unchanged. The named image identifies the host result; later component-only
module-namespace definition coverage does not relabel that image.

Reproduce with the managed host (no editor download required):

```sh
MSYNC_TEST_SERVER_PATH="$PWD/.temp/v070/msync-tooling-host" \
MSYNC_TEST_VSCODE_EXECUTABLE="$PWD/editors/vscode/.vscode-test/vscode-linux-x64-1.144.0/code" \
MSYNC_TEST_VSCODE_VERSION=1.144.0 \
xvfb-run -a npm run test:integration --prefix editors/vscode
```

Detailed disposable host evidence is under `.temp/vscode-host-C61jvG`.
