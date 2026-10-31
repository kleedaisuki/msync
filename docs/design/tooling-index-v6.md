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
