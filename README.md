# msync

msync is a typed file-synchronization language. The v0.9 implementation uses C# and .NET
Native AOT, with a brace-based expression language, source-backed standard
library, typed semantic HIR, static builtin layouts and a native-IR interpreter. The previous Rust
implementation is retained in [Git history at v0.7.3](https://github.com/kleedaisuki/msync/tree/v0.7.3),
not in the current checkout or distribution.

## Build and run

Download the complete Native AOT package for your platform from
[GitHub Releases](https://github.com/kleedaisuki/msync/releases) and use the
matching VSIX for VS Code.

Development requires the .NET SDK pinned in `global.json` (currently 10.0.403).
A published executable does not require an
installed .NET runtime. Native AOT publishing additionally requires the platform
native compiler toolchain documented by
[Microsoft](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).

```sh
dotnet build msync.slnx
dotnet run --project src/Msync.Cli -- check sync.msync
dotnet run --project src/Msync.Cli -- run sync.msync
python3 packaging/build.py --rid linux-x64 --smoke
python3 scripts/check_local.py --journey cli
```

Source scripts can run directly without a separate compilation step. On
Linux x86-64 and ARM64, and on Windows x86-64, eligible hot numeric regions
can promote to the VM's embedded native backend; no compiler installation
is required at runtime. Set `MSYNC_JIT=off` to use the interpreter tiers. See the
[execution tiers](docs/design/tiered-execution-v086.md#embedded-native-numeric-backend)
for the supported operations and profile modes.

The Python packaging command publishes the native executable, stages standard
sources and license notices, checks version agreement, and produces a portable
archive under `dist/`. Windows packaging uses `--rid win-x64` on Windows and can
build the existing per-user installer when Inno Setup is installed. The
[packaging note](docs/design/packaging-v080.md) describes the payload and
installation lifecycle. Published older release tags and assets are unchanged.
The local-check command builds with strict static/AOT analysis, checks only
changed C# formatting, and runs explicitly selected real-process journeys.

[Benchmarks](BENCHMARK.md) describes the maintained performance suite;
[research status](RESEARCH.md) separates merged results, open investigations
and possible directions.

## Built-in reference

`msync skill` prints a compact usage skill with links to progressively deeper
topics. The reference is embedded in the executable, works offline, and remains
available even when configuration is invalid:

```sh
msync skill
msync skill language functions
msync skill language scopes
msync skill language numbers
msync skill language concepts
msync skill library fs
msync skill workflow artifacts
msync skill > SKILL.md
```

Use `msync help COMMAND` for command options or `msync help syntax` for the
language guide. Terminal help and source diagnostics use the terminal's own
color palette; `--color never` or `NO_COLOR` disables it. Redirected skill output
is plain Markdown by default. The VS Code extension uses inferred function and
Scope roles for highlighting, signatures, and member completion. Its formatter
targets 80 columns while preserving literals and structural indentation.

## Expressions and bindings

Indentation has no semantic role. Braces delimit blocks; `;` terminates a
statement and discards its value. The final unterminated expression is the block
result. An empty block or a terminated final statement returns `null`.

```rust
fn copy_one(source: Path or String, destination) {
    source -> destination
};
let codex = ~/.codex;
let destination = backup;
let succeeded = copy_one(codex, destination);
```

The transfer operator returns `Bool`. Filesystem failures are reported with the
actual cause and produce `false`; cancellation remains cancellation. A discarded
failed transfer that remains in the execution plan makes the command fail.
Unobserved transfers overwritten before any read can disappear before execution.
A regular file uses an owned temporary
file beside its destination and atomic replacement, with at most one file sync.
Directory or finite-cohort failures may leave completed members in place; retry
is an ordinary new transfer, not a recovery or rollback transaction.

`let` declares a mutable location with a fixed inferred or annotated type, such
as `let value: Path = ...`. Assignment changes its value, never its storage type.
Scope aliases share mutable fields; nullable fields require `?`, for example
`scope { filename?: null }`. Stores solve its non-null payload type at compile
time, not by widening types during execution.
Function arguments are generic by default; annotations constrain admitted
specializations. Functions are values, can capture lexical bindings, call
recursively, and be passed to callback-taking functions.
Inside a function, `global root, .codex;` explicitly imports prior lexical
ancestor locations captured at definition time. It shares mutable locations,
not process-wide variables, and does not search callers or future declarations.
Implicit captures remain supported. See `msync skill language functions`.

Return constraints use `-> Type` after the parameter list for named and
anonymous functions. A nullable result is written `-> Path?`, for example.
The constraint checks reachable block tails and explicit `return` expressions;
Path/String conversion changes the runtime result as well as its inferred type.

```rust
let replace = fn(file: Path or String, destination) -> Bool {
    file -> destination
};
let selected = [input.txt, settings.toml];
for file in selected {
    replace(file, backup/file)
};
```

Names such as `.codex` are legal lexical bindings. A matching binding is used
before bare-name Path fallback; completing that variable does not turn it into a
directory name or append a separator.

Type names include `Bool`, `String`, `Path`, `Range`, `Function`, `Scope`,
`Process`, `Connection`, `Null`, and `Type`. `Range<T>` specifies an item type, `|`
specifies a union, and `?` explicitly permits null. Numeric literals and `Number`
use IEEE 754 binary64. Decimal (`1_024.5`), exponent (`1e3`), hexadecimal
(`0xff`), binary (`0b1010`) and octal (`0o17`) literals are supported, along with
`+`, `-`, `*`, `/`, `%`, `**` and unary signs. Only Path/String convert implicitly;
write `"42" as Number` for other explicit conversions. `Any` admits the full
value-type union.

Named structural concepts express required members and operation signatures
without introducing user-defined runtime types or an `impl` registry:

```rust
concept Named {
    name: String,
};
concept NamedAction {
    Named,
    run: fn(value: String) -> String,
};
fn apply(action: NamedAction, value: String) -> String {
    action::run(value)
};
scope Echo {
    name: "echo",
    run: fn(value: String) -> String { value },
};
let message = apply(Echo, "hello");
```

Requirements are checked against concrete structure at specialization. Declare
associated types where their relationship first appears:

```rust
concept Product {
    first?: typename First,
    second?: typename Second,
};
```

The standard concepts describe products, optional values, sums and explicit
policies without adding runtime type kinds. Mathematical laws such as
associativity are not inferred from operation signatures. See
`msync skill language concepts`.

Unmarked process calls launch and wait. `let process = release ./tool();`
keeps that Process live; `acquire process;` joins only it. `release` is a before
ordering fence, `acquire` is an after fence, and `acqrel` applies both. Bare
`release;` is not sticky; bare `acquire;` joins the current invocation's pending
work. See `msync skill workflow` for process lifetime and ordering details.

## Nullability and exhaustive matching

Null does not participate in boolean conditions, ordering, or `==`/`!=`.
Use `is null` or `is not null`. A nullable binding requires explicit admission,
and every use requiring a non-null value must be narrowed first. The postfix
`value?` test means **`value is null`**, not "value is present".

```rust
let file? = if exists ~/.codex { ~/.codex } else { null };
if file? {
    // No file was selected.
} else {
    file -> backup/codex
};
let label = match file {
    null => { "missing" },
    _ => { "present" },
};
```

`match` must cover every possible input. Boolean and nullable unions can be
covered explicitly; open String/Path domains and regex cases need a fallback.
Regex patterns are written `<pattern>`. A Path matched against a regex uses its
native string spelling. Types may be matched too. `decltype(expr)` infers the
operand's type without evaluating it:

```rust
let destination = match decltype(~/.codex) {
    Path => { backup/codex },
    _ => { backup/other },
};
let name = match input.txt {
    <.+[.]txt> => { "text" },
    _ => { "other" },
};
```

## Paths, connections, and scopes

Bare path operands remain paths. Quoted strings remain data: `"hello" -> out`
writes literal UTF-8 bytes, while an explicit path join or connection qualifier
constructs a Path. String/Path conversions are available in path-compatible
operations; they do not silently reinterpret a literal transfer source as a
filename. Strings retain literal backslashes, doubled quote delimiters, and
actual newlines:

```rust
"D:\Code\agents\target" -> windows-spelling.txt;
"say ""hello""" -> quoted-content.txt;
let source = ($pwd @ pwd) / "a file with spaces.txt";
source -> backup/source;
```

`$local`, `$wsl`, and `ssh://host` are `Connection` values, not lexical scopes.
Native OpenSSH configuration, aliases, agent use, and credential prompts remain
owned by the native SSH client. An unselected branch does not authenticate an
endpoint. One finite remote transfer owns one native connection/channel,
including metadata and bytes.

The SSH/WSL endpoint provides a POSIX `sh` and GNU `tar`, `findutils` and
`coreutils` (`stat`, `touch`, and the standard byte utilities). The transport
uses their archive, selector and nanosecond-metadata operations; they are normally
present in Ubuntu-based WSL distributions. A minimal Alpine endpoint needs the
corresponding packages (`apk add tar findutils coreutils`). They are endpoint
tools, not dependencies of local-only copying or the installed .NET AOT runtime.

```rust
let remote = ssh://forge;
for file in ~/.codex/<.+> {
    file -> remote @ file
};
```

A lexical Scope selects declared members instead of qualifying physical paths.
Write `connection @ path` to select a physical endpoint; `::` selects lexical
Scope members only. Named Scopes are shared singletons; anonymous `scope { ... }`
expressions construct fresh instances. Both functions and scopes can be anonymous:

```rust
scope Package {
    input: input.txt,
    enabled: true,
    copy: fn(file, destination) { file -> destination },
    nested: scope { label: "codex", },
};
let alias = Package;
alias::copy(alias::input, output.txt);
```

A Path is callable. Calling it executes a process using exact argument values
rather than reparsing a shell command, returning a `Process` value. The
`$std::process` functions inspect stdout, stderr, success, and exit code.

## Standard library and native functions

`stdlib/*.msync` files are executable compiler input embedded in the binary.
The modules include filesystem/IO, Path/String/Number operations, Range
algorithms, live processes, containers, text construction, regular expressions,
data composition, metaprogramming and terminal utilities. `msync skill library`
lists the embedded modules and progressively displays their actual declarations.
Use `msync skill library containers Set new` for one operation, or
`msync skill library console` for a runnable standard-stream recipe.

Named Scope families use PascalCase; associated functions use snake_case and
take their instance explicitly. Instances store data and selected policies,
rather than duplicating a set of method closures:

```rust
let List = $std::containers::List;
let files = List::new([./input.txt]);
List::push(files, ./settings.toml);
let before = List::snapshot(files);

let Set = $std::containers::Set;
let numbers = Set::new(Number);
Set::insert(numbers, 42);

let report = $std::files::File::new(./report.txt);
$std::files::File::write(report, "ready");
```

`$std::data` provides Pair, Option, Result and Either families. An absent Option
is native Null; `Option::some(null)` remains a distinct present value. The
`$std::meta` queries support type-directed composition without executing the
function or member access being inspected. For explicit navigation, a
`$std::ranges::Cursor` owns a finite snapshot position; ordinary `for` and lazy
adapters still stream their inputs. In the REPL, terminal utilities such
as `ls()` and `cat(./report.txt)` are available directly; scripts use
`$std::shell` explicitly.

Text formatting is separate from I/O:

```msync
$std::io::println($std::text::format("{}: {:.2f}", "Total", 12.5));
let line? = $std::io::read_line();
```

`read_line` distinguishes EOF (`null`) from an empty line (`""`).
`stdin_lines` streams input once; `eprintln` writes to standard error.

Pure compositions use ordinary functions; host-only operations use explicitly
typed native declarations:

```rust
let fs = $std::fs;
if fs::is_directory(input) {
    fs::create_directories(output);
    input -> output/.
};
let keep_text = fn(file: Path) {
    match file { <.+[.]txt> => { true }, _ => { false } }
};
for file in $std::ranges::filter(input/<.+>, keep_text) {
    file -> output/file
};
```

`native fn exists(path: Path or String) -> Bool;` binds a declaration to the
registered host capability in its module context. The declaration is not a
shell command or an unrestricted dynamic library loader. The registry owns
arity, types, dependent range results, and effect classification. Checking and
compilation do not execute filesystem or process effects.

Optimization defaults to `-O2`; `-O0` retains unoptimized typed IR. `-O1` runs
local cleanup, lifetime/placement analysis, CFG compaction and typed temporary-slot
allocation. `-O2` also propagates must-facts across blocks and inlines proved
transparent static calls within bounded code-growth budgets; `-O3` admits larger
callee and growth budgets. These are native-IR policies, not a promise that a
higher level is faster for every workload. See `msync skill workflow artifacts`
and [research status](RESEARCH.md) for the contracts and remaining work.

## Typed compilation pipeline

```text
.msync -> syntax and lexical binding -> semantic analysis / specialization
                                                       |
                                              immutable Typed HIR
                                                |            |
                                           .mhir export   pure lowering
                                                             |
                                      typed LLVM-derived IR -> O0/O1/O2/O3 optimization
                                                             |
                                                   relocatable HPIR .mso
                                                             |
                                     selected contextual observations and effects
                                                             |
                                                    executable HPIR .mexe
                                                             |
                                       VM execution / parallel scheduling / resources
```

```sh
msync check sync.msync
msync run sync.msync
msync compile sync.msync             # saves sync.mso
msync run sync.mso
msync compile sync.msync --prepare   # saves sync.mexe directly
msync run sync.mexe
```

Plain `compile` pays the compilation cost up front without capturing the
execution environment. `--prepare` also does contextual preparation, which
may freeze resolved file selections. By default the artifact is saved beside
the input; use `-o FILE` to choose its path or `-o -` for binary stdout.
For compiler tooling, `compile FILE --emit hir` saves `.mhir`; use
`disassemble FILE` to inspect an artifact.

Semantic analysis resolves declarations, types, storage constraints, captures,
call targets and evaluation policy before executable representation selection.
The compiler optimizes the typed graph and selects one physical HPIR stream;
binding does not recover type, frame or call facts from its operands. The lowerer consumes no syntax tree and does
not call back into type inference. Editor queries use semantic observations,
not speculative IR compilation.

`scope { ... }` allocates builtin storage on the logical heap and binds its
namespace fields to statically laid-out locations. Scope is not a generic
runtime object. String, Path, Connection and the other builtins have complete
native representations; captures distinguish values from locations. Compiler
and VM may eliminate or coallocate physical storage while preserving identity,
aliases, effects and resource lifetimes. There is no source allocator or
`Storage` type. See [HPIR](docs/design/hpir.md).

A self-contained `.mhir` can be inspected, rewritten and lowered after deleting
its source. Its source text is optional diagnostic data, not executable input;
live session values and credentials are not stored in it. HIR cannot be
reconstructed from `.mso` or `.mexe`: export it before native IR lowering.

Both binary stages execute without the original source. A `.mso` is prepared
for the selected execution context. Binding captures selected observations,
records closed member lists and links prepared transfer dependencies; genuine
runtime observations and work stay live. A `.mexe` carries one bound HPIR
program. It can be inspected and
submitted repeatedly, but its filesystem observations are not validity promises.
Prepared selections are frozen: adding a matching file later does not add it to
that recorded transfer. VM executes the received operations, schedules parallel
work and reports actual errors; it does not detect stale plans or automatically
replan them. Live handles and credentials are never stored. The runtime executes
typed IR on its own native stack and heap, never debug source or serialized AST.
Earlier Native objects and executables must be rebuilt from source.

The six C# projects separate model, analysis and execution responsibilities:
`Core` owns native representations, layouts and portable IR; `Hir` owns semantic programs, persistence and pure
lowering; `Compiler` owns source semantics; `Optimization` owns IR transforms;
`Runtime` contains the contextual backend and owns execution/resources; `Cli` owns commands and editor/terminal
hosting. The graph is acyclic and Native AOT still links one native executable.
See [Typed HIR and phase ownership](docs/design/typed-hir-v081.md) for the
information retained and discarded at each boundary, and the
[architecture map](docs/design/architecture-v080.md) for project dependencies.

## Configuration and editor

`msync config` prints effective TOML settings and their origins without creating
state. `--config FILE` selects a required configuration; `--no-config` disables
discovery. Explicit command-line values override the selected configuration.
`--help`, `--version`, and `--language-server` do not depend on configuration.
See [the configuration contract](docs/design/configuration-v6.md) for the
established configuration schema.

`msync fmt FILE` formats using the actual parser; `--check` checks the layout and
`--stdout` prints a preview. `msync repl` keeps bindings between submissions.
The VS Code extension under `editors/vscode` provides native syntax coloring and
starts `msync --language-server` for typed diagnostics, formatting, completion,
hover, navigation, document symbols, and semantic tokens. Standard-library
navigation uses versioned virtual documents backed by the embedded sources.

## Migration from earlier releases

The current checkout and packages contain only the C# implementation. Historical
Rust sources, standard modules and tests remain available at the immutable
[`v0.7.3` tag](https://github.com/kleedaisuki/msync/tree/v0.7.3); they are not a
build or execution dependency. There is no legacy command or executable routing.
New `.mso`/`.mexe` artifacts use Native IR format 2. Typed HIR uses its
independently versioned format 4. Recompile older artifacts from source;
language semantics and stdlib URIs remain versioned separately at 0.8.0. Scripts use
`fn`, `let`, brace blocks and semicolons instead of
`symbol`/`boolean`/`macro`, indentation, `:=`, and `=>` bindings. `async`, `await`,
and `delete` are not language keywords; deletion is an ordinary native function.

Architecture, language, runtime, packaging, and measured validation live under
[`docs/design/`](docs/design). GPL-3.0-only; see [`LICENSE`](LICENSE).
