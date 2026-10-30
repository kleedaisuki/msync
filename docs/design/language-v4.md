# Typed language and barrier-based execution

This filename is retained for existing links. Its current contract is the
**v0.5 breaking update**: transactions are removed and directional synchronization
barriers define the interpreter's basic blocks. Integration/verification status
is tracked separately; a design contract is not a successful release gate.

The default structured syntax is `0.5`, also selected explicitly by
`--syntax 0.5`, REPL `/syntax 0.5`, or an initial `# msync: 0.5` header.
This is the only CLI grammar: both `0.3` and transactional `0.4` are rejected
with migration guidance. An old wholly quoted filename must become an explicit
path, for example `$pwd / "file name"` or `$local::"file name"`; bare quotes now
always construct literal data. The CLI does not route old scripts around the
common statement publication/recovery owner. Internal flat parser/compiler
primitives are implementation components, not alternate public CLI semantics.
A requested grammar and source header must agree, and a header cannot
retroactively change earlier code.

## Static compilation boundary

`check`, `compile`, `plan`, and `run --dry-run` perform parsing, typed lexical
expansion, IR validation, block partitioning, and machine-independent graph
optimization. They do not inspect endpoint kinds, canonicalize physical paths,
resolve executable PATH entries, launch transport helpers, or initialize an
executor. Reading the requested source and explicitly enabled telemetry are
ordinary CLI IO, not endpoint discovery.

A missing input can legitimately be created by an earlier program. An existing
input can legitimately be deleted or replaced before its block executes.
Consequently the interpreter contextualizes **only the current basic block**,
using the actual state after preceding blocks. Pure lexical/context substitution
must not be confused with upfront filesystem linking. An exported object carries
typed semantic IR and debug provenance, never metadata observations or open
resource proofs.

The compiler commits candidate REPL bindings only after the complete submission
passes static checks. A subsequent IO failure does not undo those definitions.
A parse/type failure commits none of the candidate declarations. Static `/check`
and `/plan` inspect a compiler snapshot without changing session bindings.

## Values and paths

| Form | Meaning |
| --- | --- |
| `name := expression` | Typed value binding |
| `space => endpoint` | Namespace/endpoint binding |
| `Program => executable(arguments)` | Named program launch, implicitly awaited |
| `source -> destination` | Typed transfer |
| `base / component` | Typed path composition |
| `space::item` | Namespace item lookup |

Bare double-quoted expressions are UTF-8 literal data. They do not become file
reads because a same-named file exists. Qualified/joined components provide
explicit path context:

```text
text := "file name"
path := $pwd / "file name"
text -> $pwd / "message.txt"
path -> $pwd / "copy.txt"
```

Literal destinations are read-only and rejected. Literal whitespace is
preserved. Path-component boundary whitespace is rejected while interior spaces
are valid. The formatter must not simplify a qualified quoted path to a bare
literal. String decoding retains original-byte maps so an escaped token still
gets an exact source label. No value is interpolated back into source text.

Unqualified path values remain endpoint-polymorphic where declared that way:
`codex := ~/.codex` can be used with both `$local::codex` and `$wsl::codex`.
A contextual local value cannot silently be reinterpreted as a guest path.

### Context namespaces

`$pwd::pwd` is the logical cwd; `$pwd / "child"` explicitly composes under it.
Contextual qualification of a declared relative value, such as `$pwd::des`,
anchors that value under the namespace. A namespace alias follows later context
changes; a path captured with `:=` retains its declaration-time value.

`$file::this` and `$file::parent` require a named source/module context. Stdin or
a direct REPL submission does not substitute cwd. Source identity is a logical
absolute path, not a canonicalized filesystem target. Joining below a file-valued
item is rejected, including through aliases. `$env` and `$path` remain explicit
lookup capabilities; filesystem/executable readiness belongs to evaluation of
the relevant block.

Guest `~` is the endpoint user's home. SSH login-relative paths are not rebased
under the client's cwd. Remote-relative contexts must be represented explicitly,
not synthesized with a guessed `/mnt/<drive>` translation or global `chdir`.

### Destination intent

| Suffix | Meaning |
| --- | --- |
| None | Exact pathname; reject an existing directory |
| `/` | Container: append basename, merge directories without file clobber |
| `/.` | Explicit directory-tree replacement |

These are distinct typed intents, not path-cleaning accidents. A missing exact
directory target may be created from a directory source. Replace-tree removes
unmentioned old children; container merge preserves them. Selectors match whole
immediate-child basenames with Rust `regex`, copy selected directories
recursively, and require a container target. No match is a successful no-op.

## Programs and barriers

Programs use argv directly; invoking a shell is explicit:

```text
Program => $path::bash("-c", "ls")
```

This is equivalent to:

```text
async await Program => $path::bash("-c", "ls")
```

The runtime always launches asynchronously, but this default statement does not
finish until its child completes. Explicit launch-only syntax is:

```text
async Program => $path::bash("-c", "ls")
Program::stdout -> stdout.txt
await Program
```

The AST represents modifiers as a scheduled statement with independent
`asynchronous` and `awaited` flags, plus standalone `async` and
`await [Program]` forms. They do not introduce a suite or require pairing.

| Construct | Ordering obligation |
| --- | --- |
| `async STATEMENT` | Earlier work cannot sink past its start boundary; later independent work may hoist |
| `await STATEMENT` | Later work cannot hoist before its completion boundary; earlier independent work may sink |
| `async await STATEMENT` | Both obligations |
| `async` | Standalone start-side boundary |
| `await Program` | Wait for this program and connected port work |
| `await` | Wait for outstanding work |

The compiler lowers boundaries explicitly, normalizes their original one-way
constraints, then partitions the optimized IR and constructs each block's
dependency graph. It can merge file-only source regions without moving IO across
an opaque program launch or actual join. The interpreter
finishes the previous block before contextualizing the next. A launch block's
completion means the async process is admitted and owned, not that it has exited.
Similarly, admitting a background stream does not mean waiting for EOF.

Inside a block, proven-independent finite operations may overlap or reorder.
Streams retain consumption and co-start edges: opening complementary channel
halves must not be transformed into a producer-completes-before-consumer-opens
cycle. Port reads have typed dependencies on their process/flow, rather than an
invented filesystem-wide effect. External program effects remain opaque; a
barrier, not optimistic pre-execution metadata, orders observations that need
those effects to be complete.

Ordered block admission does not make an explicitly asynchronous child finish.
Reading a file created by that child needs `await Program` before the read (or
the default unmarked synchronous call). Otherwise an absent file is a legitimate
runtime race, not a reason to preinspect future blocks or invent a completion
edge from opaque shell text.

`stdin`, `stdout`, and `stderr` are byte ports. `pid`, `exit_code`, and `meta` are
typed process values. Output drains run while the child is alive, including
when consumers occur after a wait, so OS pipe capacity cannot deadlock process
completion. Exit status is data; nonzero does not itself mean transport failure.

## Lexical macros and structured child ownership

```text
macro generate(output):
    async Generator => $path::bash("-c", "printf generated")
    Generator::stdout -> output

generate($pwd / "generated.txt")
```

At definition, retain the body AST, formal declarations, source identity, and a
snapshot of the visible environment. At invocation:

1. Resolve actual arguments as typed caller values, not text substitutions.
2. Check arity and duplicate formals with exact call/definition spans.
3. Allocate a fresh lexical frame and fresh process identities.
4. Resolve locals there. `global x` imports only the definition-time `x`.
5. Inline semantic operations and retain call/definition provenance.
6. Insert a wait at macro exit for each process this expansion started
   asynchronously and did not already await.

An explicit wait discharges that pending child obligation. A macro cannot leak
an unfinished local async program merely by dropping its binding. A macro exit
does not wait for unrelated caller-owned programs. Repeated expansion must not
reuse a prior invocation's process identity. Nested macro definitions remain
outside the current language contract.

`global` outside a macro, missing definition-time imports, conflicts with formals,
and use after a local declaration receive dedicated diagnostics. Bare unbound
path names retain their path meaning, but program/namespace access requires a
binding of the corresponding type.

## Publication and lifecycle boundaries

`transaction`, isolation modes, `commit`, and `rollback` are removed from the
active grammar, compiler IR, and runtime model. They are not compatibility no-ops.
Finite file statements own publication/recovery state only for that statement;
previous successful statements survive later failure. Exact crash guarantees
and backend limits belong to [statement-atomicity.md](statement-atomicity.md).
For multi-target/tree publication, recovery before new runtime effects uses the
same configured scratch root and settles the whole old or whole new state;
this is not instantaneous multi-path visibility to outside observers before
recovery. Unix synchronizes data and directory metadata. Windows provides the
process-crash recovery contract without a portable arbitrary-storage power-loss
durability claim. Removing the pending recovery root defeats discovery and is
not a cleanup operation to perform while a statement is unsettled.

Streams and external program side effects are excluded from finite-file crash
rollback. Programs use the real environment without a Sandboxie/bubblewrap view.
Normal stop/cancellation stops admission, cancels/drains flows, and waits for
started children to exit themselves; it does not kill them on first EOF or
silently drop their handles. Forced OS termination/power loss is outside that
process-lifetime promise.

REPL `/kill` explicitly opts into destructive termination through the runtime's
force-stop operation. It can leave external program effects partial and is not
a recovery guarantee. The command is available only when the REPL is accepting
input, including while background work is active; a foreground graceful drain
does not invent a second-Ctrl-C force-stop path. An operator can still force
termination externally, outside the natural-exit guarantee.

## Layout, formatting, and diagnostics

Only macro headers introduce indented suites. Blank/comment-only lines do not
change indentation. Tabs use eight-column stops; ambiguous mixed indentation is
rejected. Canonical output uses four spaces per suite. CRLF source byte offsets
are retained. Quotes, selectors, character classes, and call parentheses have
lexical state, so commas/comments/operators inside them are not split as syntax.

Formatting edits the source file by default. `--check` and `--stdout` are explicit
read-only modes; compiled objects are rejected without modification. Comments
and typed operand meaning survive formatting, including async/await modifiers.

Every lowered instruction retains its primary operation/operand and expansion
origins. Runtime provenance is not error classification: IO failures remain
evaluation/execution errors even when they carry a source label. Environment
errors without a real location never fabricate `Span::default()` as line 1.
See [runtime diagnostics](runtime-diagnostics-v4.md).

## Design references and acceptance

The [MLIR canonicalization contract](https://mlir.llvm.org/docs/Canonicalization/)
provides the useful separation: rewrites should converge and correctness must
not depend on running the canonicalizer. msync applies that principle inside
the user's explicit barrier regions, without importing MLIR as infrastructure.
[Tokio's process API](https://docs.rs/tokio/latest/tokio/process/index.html)
provides mature asynchronous child/pipe ownership; dropping a future alone is
not a process-completion receipt. The
[Python indentation reference](https://docs.python.org/3/reference/lexical_analysis.html#indentation)
remains the small layout-stack precedent, not a reason to adopt Python grammar.

Required journeys include formatting round-trip and idempotence, macro child
wait insertion, exact source provenance, no helpers during static compilation,
compile→object→run without source reparsing, same-block graph optimization,
post-program file creation/deletion observed only by later blocks, and graceful
stop that leaves a started child's completion side effect intact. Crash recovery
needs actual killed-process fixtures; ordinary unit success is insufficient.
