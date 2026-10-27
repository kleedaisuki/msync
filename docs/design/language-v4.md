# v0.4 language frontend: typed blocks, calls and lexical expansion

Status: frontend implementation and runtime integration contract, not a claim
that all runtime paths below already execute. The user-supplied v0.4 example is the semantic target. This note owns
frontend rules; transaction enforcement and effect scheduling belong in the
companion architecture and transaction notes.

## Decisions that unblock implementation

1. Add a structured `Document` API and typed expressions. Keep `syntax::Script`,
   `StatementKind`, `Compiler::compile*`, `Program.operations` and the legacy
   parser/formatter entrypoints available with their existing contracts. A new
   frontend must not squeeze structured control into fake copy operations.
2. In the v0.4 language, every bare double-quoted expression is UTF-8 literal content,
   including value bindings and destinations. Literal destinations are read-only.
   An explicit path join (`./"file name"`) or qualifier, for example
   `$local::"folder name/input.txt"`, makes a quoted source a path. Legacy syntax
   APIs still treat a whole quoted operand as a path. The user authorized the
   default-v0.4 breaking update; legacy mode is explicit. Never choose content
   versus path by checking existence.
3. In v0.4, `$pwd::des/agents/.` resolves the first item structurally, then anchors
   the resulting relative value under the invocation cwd. A macro formal `des`
   is therefore usable there. `$pwd::pwd` and `$pwd::pwd/child` retain their existing
   cwd-item meanings. This is a versioned frontend rule, not an accidental fallback
   inside the old strict namespace resolver.
4. `global name` or `global a, b` imports bindings from the macro's declaration-time
   environment. Macro parameters and locals are isolated from caller locals.
   Macro expansion manipulates values and scope IDs, never interpolates strings.
5. `p => executable(arg, ...)` binds a program space. A bare call statement invokes
   a macro. Process arguments form an argv vector; there is no shell re-parsing.
6. Destination `/.` is an explicit replace-tree intent, different from both
   ordinary overwrite and trailing-slash container/merge intent. Carry that intent
   as a typed mode to the runtime; do not silently trim the suffix and reuse the
   old existing-directory rejection.

The quote distinction is unavoidable: the same bytes `"a" -> b` cannot both mean
"read file a" and "write literal a" without a language/context distinction. The
new feature is requested, but a nonexistent-file heuristic would turn a typo into
a successful data-changing command. Keeping a legacy API and an explicit new
language boundary is the safe solution; it is not evidence that the CLI boundary
has already been implemented.

## Existing code constraints

`src/syntax.rs` currently scans physical lines, decodes both operands to `String`,
and retains optional source maps in `OperandSource`. The decoder loses whether a
decoded value is content or a path. Its `StatementKind` has four variants:
endpoint, alias, copy and comment. The formatter can safely re-quote because every
operand currently has path/binding semantics. That assumption is false for v0.4.

`src/compiler.rs` owns two copy-on-write `Arc<BTreeMap<...>>` environments. It
commits a cloned candidate only after a successful submission and emits an ordered
flat `Vec<CopyOp>`. These are useful legacy leaves, not a sufficient representation
for transaction checkpoints or live program identities.

`src/editor.rs::Input::read` deliberately splits accepted multiline paste into
physical submissions. `LineHelper::validate` currently only rejects controls;
`src/repl.rs::repl_submit_inner` parses and executes one submission immediately.
Blocks must be accumulated above physical-line input before execution. Do not
undo the `buffer-redux` typeahead preservation fix while adding block input.

`src/highlight.rs` is a tolerant display lexer, not execution grammar. It can add
keyword/delimiter categories without rejecting incomplete headers. Parsing,
completion, formatting and highlighting must agree on vocabulary, but a partially
typed `transaction A(` must still be displayable.

## Proposed AST boundary

The names below are an interface sketch rather than a frozen Rust public API.
All spans are original half-open UTF-8 byte ranges, including nested arguments.

```rust
struct Document {
    statements: Vec<Node>,
}

struct Node {
    kind: NodeKind,
    span: Span,
}

struct Name {
    text: String,
    span: Span,
}

enum NodeKind {
    BindSpace { name: Name, value: Expr },
    BindValue { name: Name, value: Expr },
    Copy { source: Expr, destination: Expr },
    Macro { name: Name, parameters: Vec<Name>, body: Vec<Node> },
    Invoke(Expr),
    Global { names: Vec<Name> },
    Transaction { name: Name, isolation: Isolation, body: Vec<Node> },
    Commit,
    Rollback { target: Option<Name> },
    Comment(String),
}

struct Expr {
    kind: ExprKind,
    span: Span,
    source: OperandSource,
}

enum ExprKind {
    Reference { qualifier: Option<Name>, path: String },
    Literal(String),
    Call { callee: Box<Expr>, arguments: Vec<Expr> },
}
```

`Reference` intentionally does not decide whether `p::stdout` is an endpoint item,
program port, imported program or formal parameter. The compiler resolves the
qualifier to a typed space. Path suffixes must be segmented before formal lookup;
resolving typed operands is valid, replacing arbitrary substrings is not. The
implemented `ExprKind::Join` retains the operator span, base, optional suffix,
and destination intent. Compact and spaced joins use the same AST: a bound bare
operand resolves lexically; quote a fixed component (`src / "agents"`) instead
of capturing a binding named `agents`.
For example formal `des` affects `$pwd::des/out`, not `destination` or
`folder/des/out`.

Store `DestinationMode::{Overwrite, Container, ReplaceTree}` in lowered transfer
IR. `ReplaceTree` applies only to directory destinations; regular/stream misuse
needs a targeted error on `/.`. Selectors already require container output and
must not silently acquire replace-tree behavior.

The compiler can lower purely legacy leaves to existing `CopyOp` structures.
Literal, program and transaction instructions must remain typed in HIR. No
temporary filename convention should be used to masquerade literal values or
program ports as ordinary input files during semantic compilation.

## Layout and tokenization

Use one scanner with lexical states for strings, selectors/regex character
classes, and balanced call parentheses. Split operators, commas and namespace
separators only in their valid state. A comma in `"a,b"`, a regex class or a nested
call is not an argument separator. A `#` in a URL fragment retains the existing
non-whitespace-delimited behavior.

Recommended layout rules:

- Headers end in an unquoted, unparenthesized `:`. Only macro and transaction
  headers introduce suites.
- Maintain an indentation stack initially containing zero. A nonblank logical
  line may increase indentation only after a suite header. A dedent must match an
  earlier stack level. EOF closes complete suites.
- Blank and comment-only lines do not alter indentation. Retain comments in the
  AST at the lexical suite that contains them; a header's trailing comment becomes
  the first retained comment inside that suite on canonical formatting.
- Accept tabs and spaces with Python-compatible tab stops of eight columns, but
  reject ambiguous mixed indentation (track both tab-expanded and alternate
  indentation widths). The user supplied tabs, so a spaces-only parser is not a
  compatible interpretation. Canonical output uses four spaces per suite level.
- Preserve original CRLF byte offsets. Do not normalize source before span
  construction.
- Balanced call parentheses may support logical-line continuation. If included,
  indentation inside them is not suite indentation. An unfinished call/header
  should report its opening delimiter and an insertion point, not a generic
  missing copy operator.

This follows the small stack-based mechanism documented by the
[Python lexical analysis reference](https://docs.python.org/3/reference/lexical_analysis.html#indentation),
without adopting Python's entire grammar. No general parser generator is required
for this small statement grammar. The existing selector scanner is worth reusing,
but duplicated state machines that disagree on comments/quotes are not.

### Expression context and quoting

| Context | Bare token | Double-quoted token | Qualified quoted item |
| --- | --- | --- | --- |
| Copy source | Path/item/port reference | Read-only UTF-8 literal | Path/item reference |
| Copy destination | Writable path/port reference | Read-only literal; rejected as a sink | Path/item reference |
| `:=` RHS | Typed path/item reference | UTF-8 literal data | Path/item reference |
| Call argument | Resolved binding if present, otherwise item value | UTF-8 string argument | Typed path/item reference |
| `=>` RHS | Namespace reference or program call | Literal is not a namespace | Namespace/item reference |

`text := "file name"` therefore binds data, while `path := ./"file name"` binds
a relative endpoint-polymorphic path. `/` and `::` provide explicit path context;
they may contextualize string items without interpreting them as source text.
Path-component boundary whitespace is rejected, while interior spaces are valid.
Literal data whitespace is never trimmed. The formatter must
never simplify `$local::"a"` into `"a"`, because that changes a path into content.

Reuse the existing escape decoder and original-byte map. Empty literal `""` is
valid and writes zero bytes; an empty path remains invalid. Emit literal UTF-8
bytes exactly, without an implicit newline. Keep unknown-backslash behavior
consistent with established Windows-path handling unless a separate strict
literal escape grammar is deliberately introduced and diagnosed.

The [Rust literal-expression reference](https://doc.rust-lang.org/reference/expressions/literal-expr.html#string-literal-expressions)
is a useful precedent for retaining decoded UTF-8 value separately from source
token spelling. The project does not need to adopt Rust byte/raw literal forms to
support the user example.

## Macro environment and expansion

At definition, store the body AST, formal declarations, definition source identity
and a persistent snapshot of the visible environment. At call:

1. Resolve actual arguments in the caller's lexical/context environment. This
   preserves a remote path's endpoint, a program handle's identity and a stream
   port's direction/consumption semantics.
2. Check arity with primary call-argument and secondary parameter spans. Reject
   duplicate formal names on the second definition token.
3. Allocate a fresh lexical frame/scope ID. Bind actual typed values to formals.
4. Resolve macro locals in that frame. `global x` imports only an `x` from the
   definition snapshot, not a new caller binding or later global declaration.
5. Inline typed operations into caller HIR, retaining definition and expansion
   provenance. Do not expose the macro frame's local declarations afterward.

`global` outside a macro, importing a missing declaration, conflicting with a
formal, or being used after a local declaration of the same name should get
specific diagnostics. Reading a caller binding without a parameter/global must
not accidentally capture it. The DSL currently also treats unbound bare names as
paths; preserve that fallback as a path value, but a name used as a namespace or
program must be bound to such a capability.

Binding identity rather than spelling prevents the usual temporary-name capture
bug; [Racket's lexical macro examples](https://docs.racket-lang.org/guide/pattern-macros.html#%28part._.Lexical_.Scope%29)
show that concrete failure mechanism. This DSL needs only lexical frames and
typed values, not Racket's arbitrary syntax-transformer machinery.

`global` resembles Python's declaration spelling, not Python's mutable global
assignment semantics. Here it imports a pre-definition binding snapshot; local
rebinding must not mutate the caller's compiler environment. Document that
difference next to the macro examples rather than relying on familiarity with
Python.

No macro declaration is legal anywhere inside a macro body, even inside a nested
transaction. Detect it in semantic validation with an enclosing-macro label.
Calling a previously declared macro from another macro need not be forbidden;
detect direct and indirect expansion cycles with an active macro-ID stack. A
configurable expansion budget is a separate resource limit, not a substitute for
cycle diagnostics.

Context proposal: `$pwd` uses the invocation cwd, while `$file` inside the body
uses the macro definition's source-file identity. Argument expressions resolve
before frame entry and therefore use the caller's `$file`. Definition snapshots
retain the distinction between captured `:=` values and contextual `=>`
capabilities. The final architecture should settle this explicitly before tests
make it accidental.

## Program spaces and ports

`p => $path::tool(args...)` is a program declaration, not a namespace alias to a
string containing parentheses. Compile the executable and arguments to typed
values. Execution owns spawning and process lifetime; `check` and `plan` never
launch a child.

| Member | Direction | Availability/value proposal |
| --- | --- | --- |
| `stdin` | Destination only | Writable child byte port; close after its producer finishes |
| `stdout` | Source only | Child output bytes, drained concurrently |
| `stderr` | Source only | Child error bytes, drained concurrently |
| `exit_code` | Source only | Stable UTF-8 decimal status after exit, no implicit newline |
| `pid` | Source only | Stable UTF-8 decimal process ID after spawn |
| `meta` | Source only | Stable UTF-8 structured metadata with a documented schema |

Type-check invalid directions on the member token, e.g. `out -> p::stdout`.
Passing `p` into a macro passes that program identity, not a request to launch a
second process. Passing `p::stdout` preserves its consuming-stream identity.
Multiple reads from `exit_code` are safe immutable-value reads; multiple consuming
reads of one stdout need an explicit policy rather than nondeterministically
sharing one pipe. Reject unsupported fan-out with a helpful message until a
documented broadcast/spooling rule exists.

Direct argv construction is important: `$path::bash("python", "run_codex.py")`
passes those two literal arguments to Bash. It does not magically mean
`bash -c 'python run_codex.py'`. Users who need shell command syntax can pass
`"-c"` explicitly; direct `$path::python(...)` is simpler. If the product wants a
different Bash convenience contract it must be explicit, not guessed per
executable name in the parser.

## Transaction syntax and lexical meaning

Parse all four isolation levels as enum values:
`read uncommitted`, `read committed`, `repeatable read`, `serializable`.
Omitting parentheses, or empty parentheses if accepted, defaults to serializable.
Canonical output prints the chosen level consistently. An unknown spelling labels
the isolation token and lists the four valid names.

Transactions do not allocate lexical frames. Ordinary declarations in their body
remain in the enclosing compiler scope. The transaction ID is a control identity,
not a namespace. Preserve the body structure through HIR so that runtime
checkpoints and optimizer barriers cannot disappear when flattening copies.

- Bare `commit` and bare `rollback` are local transaction controls; outside a
  transaction they are errors.
- Local `rollback` restores the latest commit checkpoint, or transaction entry
  when there has been no explicit commit.
- End of a successful transaction supplies the requested implicit commit.
- Top-level `rollback A1` refers to the complete historical transaction, not only
  the latest checkpoint.
- Any named rollback while inside another active transaction is rejected with a
  label on both control and transaction header. A runtime ordering guess is not
  a proof.

The compiler may bind names after rollback because there is no independent
transaction lexical environment; runtime file/resource state is what checkpoint
controls restore. Whether a program handle remains live after a rollback is a
runtime-lifetime matter and must not be silently converted into lexical scope.

## REPL, formatting and diagnostics integration

Add a block-submission accumulator in the REPL, not a second interpreter. A header
starts a pending block; show a continuation prompt and preserve indentation.
An empty line at a complete block boundary submits the block. File parsing closes
complete blocks at EOF. Ctrl-C discards the pending block without compiling,
executing or mutating bindings. A syntactically incomplete block at input EOF
reports the header/indent insertion point and does not execute its prefix.

Do not dispatch `/load`, `/cd` or shell commands as though they were DSL lines
inside a pending suite. Offer Ctrl-C to return to command mode. A pasted block
must not execute its first transfer merely because `Input::read` yielded a
physical line. Extend the existing real-PTY typeahead regression with a pasted
macro definition plus invocation and verify no data changes before submission.

Canonical formatting recursively walks typed nodes, writes four-space suites,
normalizes headers/call commas and preserves comments. No format pass executes
macros or resolves runtime metadata. Quote according to expression kind and
position, not only whether a string contains spaces. Core properties remain:

```text
semantic_ast(parse(format(source))) == semantic_ast(parse(source))
format(format(source)) == format(source)
```

Spans and comments' exact inline locations may differ after formatting; typed
literal/path distinction, block structure, isolation choices and control targets
must not.

Existing `Report`/`Label` and named renderer are adequate for new syntax. Extend
provenance with an expansion chain so a malformed macro actual labels the call
argument and its formal use. Prefer a concise primary error with a secondary
definition location over a giant expanded synthetic source dump.

## Focused verification and adoption order

Do not rerun the complete legacy matrix on every edit. First create a focused
`language_v4` target, then let GitHub Actions cover full platform regressions at
coherent integration points.

1. Parser/formatter: user's exact example, tabs and spaces, indentation mismatch,
   comment-only lines, nested transaction syntax, commas/operators in quoted
   arguments/selectors, CRLF and Unicode spans, empty literal, qualified quoted
   paths, round-trip/idempotence properties.
2. Macro lowering: endpoint-polymorphic source parameter, cwd-anchored item
   parameter, program/stream parameters, declaration snapshot versus later global
   rebinding, local/caller temporary collision, nested declaration rejection,
   direct/indirect recursion diagnostics, no exported locals.
3. Program integration: argv boundaries with whitespace/quotes/non-ASCII, no
   process spawn during check/plan, concurrent stdout/stderr draining, stdin EOF,
   reusable exit-code value, invalid port direction and consuming fan-out policy.
4. User journeys: actual formatted script run plus byte-correct directory
   replacement, named full rollback after multiple commits, local rollback to last
   commit, each isolation level with a discriminating interleaving rather than
   four enum-display assertions.
5. Actual terminal: block paste, blank-line submission, pending-block Ctrl-C,
   Unicode editing and history retaining one coherent block. Pipe input has no
   prompts and cannot execute an incomplete prefix at EOF.

Parser and compiler measurements follow functional integration. Time scanning,
expansion and lowering separately with source/token/node counts and report
milliseconds at small real script sizes; do not claim a faster parser from one
full-process startup sample. Reuse persistent environment snapshots, intern only
when repeated identifiers measurably dominate, and avoid quadratic concatenation
of expanded source because no expanded source string is required.

## Owned sources and implemented diagnostics

`source::{Source, SourceId, SourceMap, SourceSpan, LocatedReport}` own original
UTF-8 text and display identity. `pipeline::compile_named` accepts source text,
display name, compile context and planning premises. Macro definitions capture
their source Arc together with their lexical environment; a later file/REPL call
does not reopen the defining file or reinterpret definition offsets in the caller.
Distinct sources with the same basename retain distinct identities.

`Expr::source` is an `OperandSource` mapping decoded bytes back to original
tokens, including qualified quoted suffixes. Selector diagnostics and contextual
namespace member errors use that map; program-member errors label the member
token. A `Compilation` carries an origin map keyed by instruction ID, preserving
the call site, expanded definition and actual source/destination argument sites
without changing the HIR instruction layout. `instruction_report` lets a runtime
retain typed OS diagnostics and select the meaningful original operand.

`LocatedReport::render` emits one error heading and groups secondary labels by
their own owned sources, using the existing Unicode/control-safe renderer.
The basic `Report` API remains unchanged. Compatibility flattening moves a foreign
primary to its outer call context and names the definition in help instead of
placing foreign offsets inside a caller snippet.

`Session::{try_from_legacy, try_merge_legacy}` import typed compiler maps rather
than parse `/bindings` display strings. The fallible bridge rejects a legacy name
that simultaneously denotes a namespace and item, before changing any state.
Structured binding display renders data as literal data and paths through explicit
join/namespace operations; it never turns a relative polymorphic path into a
`$local`-pinned value merely to quote spaces. Container/replacement intent is part
of the internal path value, not guessed from characters inside a quoted fragment.

Focused evidence lives in `source_v4`, `join_v4`, `language_v4`, `lowering_v4` and
`pipeline_v4`: cross-submission macro diagnostics, escaped UTF-8 byte maps, arity
and prior-port ownership labels, typed binding replay, uniform literal aliases,
compact/spaced joins, namespace roots, interior spaces and delimiter protection.

Preparation uses `Diagnostic` with an optional instruction ID and a typed
`Operand` (`Operation`, `Source`, `Destination`). Inspection retains that site
through local resolution and remote metadata batches; type validation chooses the
failing operand directly. `prepare_with_context_detailed` exposes those sites,
and `Compilation::preparation_report` resolves them against owned macro and caller
sources. The original `Vec<Report>` preparation APIs remain compatibility wrappers.
This boundary must not recover operand roles by matching diagnostic wording.
Directory-mode failures identify the destination token and suggest explicit `/`
container or `/.` replacement intent before any mutation.

## Interactive work ownership

The default grammar is not the owner of running work. `ReplState` routes `/jobs`,
`/wait`, `/stop`, Ctrl-C and shutdown to the actual structured session even when
an explicitly versioned `/load` uses a different grammar from `/syntax`. A legacy
copy cannot start a second, independently budgeted executor while structured work
is active; it fails before file mutation. Pending legacy path/namespace changes
are bridged once on the next structured submission or explicit syntax switch,
not repeatedly over already-owned structured macro/source provenance.

Human job IDs identify submissions which create pending publication cohorts.
Runtime transaction IDs are the ownership key: attaching a port to an existing
cohort does not invent an independent job. Independent stream submissions can
remain live together. Completed physical cohorts are reaped from actual task
readiness; an existing but finished task handle is not evidence that a job is
still running. No `done` message is emitted for an unfinished physical flow.
Completion counters are drained as explicitly labelled session progress rather
than silently attributed to the next unrelated command. Asynchronous errors
retain their publication transaction so independent jobs select their owned
Compilation instead of rendering source offsets against `<runtime>`.
Flows and programs additionally capture their instruction origin at creation.
That owned source/operand site takes precedence over the cohort fallback when a
later input attaches a failing producer to an already-running program; replacing
the current submission's origin map cannot change an earlier flow's diagnostics.

While structured work is active, a scoped thread owns the ordinary editor read
and the main thread services ready publication work at a bounded 20 ms quantum.
Once work finishes it blocks on the input channel instead of continuing to poll.
No unsafe Send implementation or permanent detached editor thread is used.
Lifecycle notices and owned diagnostics are queued until the read returns, so
background completion cannot corrupt a partially edited terminal line. File
publication itself happens while the editor waits: EOF never requires a further
keystroke. The readiness predicate avoids creating empty execute traces every
poll. Runtime global waits also service ready later cohorts instead of blocking
all publication behind an earlier idle source.

Focused actual journeys in `repl_jobs` cover physical EOF publication before any
further input, independent job identities, a ready second flow during global
wait, actual-owner dispatch across syntax headers, cross-executor rejection,
owned-source background errors, piped EOF versus explicit quit, and no invented
completion when no work exists. The real terminal Ctrl-C observer reads the exact
accepted prefix from a private invocation stage under `.temp`, verifies native
peer EOF and target preservation, then checks both stage and invocation cleanup.

Async runtime diagnostics retain `InstructionOrigin` when a flow, deferred
publication, or program is created. `RuntimeSession::set_origins` registers only
the next submission's map; a later source-less submission cannot accidentally
reuse previous instruction IDs. An `ExecutionError` carries its owned origin,
independently of publication-cohort identity. Human-facing code renders that
origin before falling back to the cohort's compilation: one cohort may contain
ports connected by several differently named REPL inputs. Read/write flow errors
select the source/destination argument structurally, while preserving the macro
operation's definition as a related label. No source file is reopened.

Value provenance still follows a bound value's definition (including an alias
used as a join base). It is intentionally distinct from an operation's submission
source. Capturing runtime origins preserves that established choice; it does not
rebase definition offsets into whichever input happened to trigger a later wait.

The focused default-v0.4 Windows `cargo test --locked --test repl_jobs` gate
passed all twelve cases after this ownership integration. Targeted strict
Clippy also passed. The actual Windows ConPTY Ctrl-C journey passed with its
exact-prefix stage observer and physical peer EOF checks; this was not inferred
from piped stdin.

The additional native-backend provenance journey was explicitly executed in WSL,
not merely compiled or left ignored: `a_later_input_failure_keeps_its_source_inside_an_existing_program_job`
passed in 0.14 seconds. A real `cat` process starts from `launch.msync`; a later
`feed.msync` submission attaches a TCP producer which fails connection. The error
must identify line 4 of `feed.msync` and its transfer text, not the program-launch
source, while retaining job 1 and not inventing job 2. `/help` proves recovery.
This run used a private mount/user/network namespace, loopback enabled, and an
project `.temp` mounted on tmpfs (128 MiB). It is native IPC/filesystem correctness
evidence, not an ext4 disk-performance measurement. Build output stayed in
`target/wsl-performance`; the test binary was executed directly inside the
namespace so a private user mapping did not depend on traversing the host user's
Rust toolchain directory. No SSH/system configuration was changed.
