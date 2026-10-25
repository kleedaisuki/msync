# Implementation contract

The supplied `docs/dsl/spread.msync` is the semantic seed. The Rust library separates syntax, compilation and execution. Scripts and the REPL use the same pipeline. All implementation notes are in English.

## Shared interfaces

`syntax.rs` owns the public AST, parser, diagnostics and formatter described below. Root owns `compiler.rs`, CLI and integration. Runtime owner implements `runtime.rs`. No shell interpolation of untrusted local arguments; transports must quote remote arguments. Runtime defaults to real execution only when explicitly requested by CLI run/REPL. `check`, `compile`, `fmt` and `--dry-run` do not copy files.

```rust
pub struct Span { pub start: usize, pub end: usize }
pub struct Diagnostic { pub message: String, pub span: Span }
pub struct Script { pub statements: Vec<Statement> }
pub struct Statement { pub kind: StatementKind, pub span: Span }
pub enum StatementKind {
    Endpoint { name: String, value: String },
    Alias { name: String, value: String },
    Copy { source: String, destination: String },
    Comment(String),
}
pub fn parse(source: &str) -> Result<Script, Vec<Diagnostic>>;
pub fn format(source: &str) -> Result<String, Vec<Diagnostic>>;
pub fn render_diagnostic(source: &str, diagnostic: &Diagnostic) -> String;
```

Parser keeps operands as strings; spaces surrounding operators are removed; quoted operands are decoded (double quotes support escaped quote/backslash and newline/tab; unknown escapes remain literal). Comments are standalone AST statements; inline comments may be moved onto their own line but must survive formatting. Operators inside quotes/regex `<...>` are not statement delimiters. Blank lines need not survive formatting. `format` is idempotent and preserves parsed semantic statements and comments, with final newline. Token-level public lexer is welcome but not required by integration.

```rust
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Backend { Local, Wsl, Ssh { host: String, user: Option<String>, port: Option<u16> }, Http { base: String }, Path, Env }
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Location { pub backend: Backend, pub path: String }
#[derive(Clone, Debug)]
pub struct CopyOp { pub source: Location, pub destination: Location, pub pattern: Option<String>, pub span: syntax::Span }
#[derive(Clone, Debug, Default)]
pub struct Program { pub operations: Vec<CopyOp> }
#[derive(Clone, Debug, Default)]
pub struct Compiler { /* private binding maps */ }
impl Compiler {
 pub fn new() -> Self;
 pub fn compile(&mut self, script: &syntax::Script) -> Result<Program, Vec<syntax::Diagnostic>>;
}
```

Compiler is transactional: a failed submission must not mutate bindings. Endpoint aliases resolve `name => $wsl`, `$local`, `$path`, `$env`, prior endpoint aliases, ssh:// and http(s)://. Qualified paths split on `::`; absent endpoint means Local. Compiler expands path alias prefixes by whole components; a plain alias value does not force Local, so `remote::.codex/x` expands `.codex := ~/.codex` under remote. Explicitly endpoint-qualified aliases retain that endpoint and conflicting explicit qualifiers fail. Selector `<regex>` may only be the last source component, is validated by compiler, and is removed from the source path; its pattern applies to direct directory entries (full-name match). Destination regex is rejected. Destination ending `/` means preserve source basename and fail if an existing file occupies it; existing directories merge recursively. Non-directory destination overwrites files and recursively merges directories. Compiler rejects HTTP, PATH and ENV endpoint declaration misuse where appropriate; runtime expands ENV and searches PATH.

```rust
pub struct ExecutionOptions { pub dry_run: bool }
impl Default for ExecutionOptions { /* dry_run false */ }
pub struct ExecutionReport { pub copied_files: usize, pub created_directories: usize, pub actions: Vec<String> }
pub struct RuntimeError { pub message: String, pub span: syntax::Span }
pub fn execute(program: &compiler::Program, options: &ExecutionOptions) -> Result<ExecutionReport, RuntimeError>;
```

Root may extend `execute` options only in coordination. Runtime receives ordered operations, executes sequentially, expands leading `~` for local, ENV first path component against process environment, PATH source lookup against process PATH. HTTP is read-only and downloads one file; use curl as platform transport if needed, avoid library bloat. WSL and SSH use system clients, clear missing-client failures. Destination suffix `/` remains in `Location.path`. Local copies must guard same-file copies, recursive copy into self, symlink cycles and never follow destination symlinks outside the intended tree. Avoid destructive mutations before validating easy failures. All new Python, if any, must have complete type annotations.

## Scope

No deletion, watch daemon or bidirectional reconciliation in this version. Transport authentication belongs to OS clients. No implicit shell execution in DSL. CLI supports run/check/compile/fmt/repl, dry-run, stdin and helpful diagnostics. REPL preserves bindings; failed compile rolls back. `:quit`, `:help`, `:bindings`, `:reset` supported. Tests must cover seed example, roundtrip properties, real filesystem journeys and CLI/REPL. Optimize only after correctness and retain benchmark evidence.

## Delivered refinements

The user confirmed direct-child regex enumeration, persistent REPL bindings and read-only HTTP. Slash commands are the primary REPL interface (`/help`, `/?`, `/cd`, `/pwd`, `/load`, `/check`, `/plan`, `/fmt`, `/bindings`, `/reset`, `/dry-run`, `/quit`); colon aliases remain accepted. Unicode-aware located diagnostics are ANSI-free library data, with CLI color policy and Windows console adaptation isolated at the presentation boundary.

User-requested end-to-end telemetry is opt-in, local-only JSONL: one session trace with read, REPL/command, submission, parse, compile, format, execute and copy spans. Durations, parent IDs, structural outcomes and numeric measurements are recorded without paths, source, environment values or error bodies. Failed export is visible and final spans are explicitly flushed. See `telemetry.md` for the exact schema and boundary with distributed tracing.

Compiler binding maps use `Arc<BTreeMap>` snapshots with clone-on-write mutation; the transaction model is unchanged. Whole-component alias lookup exploits the name invariant instead of scanning every binding. Selector validation is deduplicated only within one compilation, avoiding persistent cache lifetime/state. See `performance.md` for controlled baseline comparisons and the untargeted-control noise, not just headline gains.

The typed intermediate plan follows the useful compiler separation in the [Rust compiler overview](https://rustc-dev-guide.rust-lang.org/overview.html): retain spans, resolve frontend meanings once, and interpret a simpler representation rather than adding independent script/REPL execution paths. The [Unison formal specification](https://www.cis.upenn.edu/~bcpierce/papers/unisonspec.pdf) is relevant for distinguishing a filesystem-state contract from its implementation; this project deliberately does not claim Unison-style bidirectional reconciliation. The concrete filesystem properties tested here are content roundtrip, overwrite idempotence, preservation of unrelated destination children, and no-clobber conflict detection before predictable mutation. This scope is useful without importing reconciliation state or deletion semantics absent from the supplied DSL.
