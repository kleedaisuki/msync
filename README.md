# msync

msync is a typed file-synchronization language. Version 0.8 uses C# and .NET
Native AOT, with a brace-based expression language, source-backed standard
library, binary bytecode artifacts, and a block interpreter. The previous Rust
implementation remains buildable in [`legacy/`](legacy/README.md).

## Build and run

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

The Python packaging command publishes the native executable, stages standard
sources and license notices, checks version agreement, and produces a portable
archive under `dist/`. Windows packaging uses `--rid win-x64` on Windows and can
build the existing per-user installer when Inno Setup is installed. The
[packaging note](docs/design/packaging-v080.md) describes the payload and
installation lifecycle. Published older release tags and assets are unchanged.
The local-check command builds with strict static/AOT analysis, checks only
changed C# formatting, and runs explicitly selected real-process journeys.

## Built-in reference

`msync skill` prints a compact usage skill with links to progressively deeper
topics. The reference is embedded in the executable, works offline, and remains
available even when configuration is invalid:

```sh
msync skill
msync skill language functions
msync skill language scopes
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
fn copy_one(source: Path | String, destination) {
    source -> destination
};
let codex = ~/.codex;
let destination = backup;
let succeeded = copy_one(codex, destination);
```

The transfer operator returns `Bool`. Filesystem failures are reported with the
actual cause and produce `false`; cancellation remains cancellation. A discarded
failed transfer makes the command fail. A regular file uses an owned temporary
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
let replace = fn(file: Path | String, destination) -> Bool {
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
support range counts and arithmetic. `Any` admits the full value-type union.

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
let source = $pwd::pwd / "a file with spaces.txt";
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
    file -> remote::file
};
```

A lexical Scope selects declared members instead of qualifying physical paths.
Both functions and scopes can be anonymous:

```rust
scope package {
    input: input.txt,
    enabled: true,
    copy: fn(file, destination) { file -> destination },
    nested: scope { label: "codex", },
};
let alias = package;
alias::copy(alias::input, output.txt);
```

A Path is callable. Calling it executes a process using exact argument values
rather than reparsing a shell command, returning a `Process` value. The
`$std::process` functions inspect stdout, stderr, success, and exit code.

## Standard library and native functions

`stdlib/*.msync` files are executable compiler input embedded in the binary.
Their namespaces are `$std::fs`, `$std::path`, `$std::string`, `$std::bool`,
`$std::ranges`, and `$std::process`. Pure compositions use ordinary functions;
host-only operations use explicitly typed native declarations:

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

`native fn exists(path: Path | String) -> Bool;` binds a declaration to the
registered host capability in its module context. The declaration is not a
shell command or an unrestricted dynamic library loader. The registry owns
arity, types, dependent range results, and effect classification. Checking and
compilation do not execute filesystem or process effects.

Optimization defaults to `-O2`. Select `-O1` for lower compilation latency or
`-O3` for larger inlining and finite-range expansion budgets; `-O0` retains
unoptimized typed bytecode. See `msync skill workflow artifacts` for the staged
optimization policy.

## Binary compilation pipeline

```text
.msync -> parse/type/narrow -> generic specialization -> typed bytecode
                                                       |
                          machine-independent O0/O1/O2/O3 optimization
                                                       |
                                               relocatable .mso
                                                       |
                         runtime binding, block admission and specialization
                                                       |
                                                executable .mexe
                                                       |
                                            interpreter + native scheduler
```

```sh
msync check sync.msync
msync compile sync.msync -O1 -o sync.mso
msync bind-ir sync.mso -o sync.mexe
msync plan-ir sync.mexe
msync run sync.mso
msync run-ir sync.mexe
```

Both binary formats execute without the original source. A `.mso` binds to the
execution context when loaded. A `.mexe` captures working directory/home/source
context; it does not persist open handles, authentication, filesystem snapshots,
or validity proofs. Loading checks the bytecode format and executable contract.
The runtime executes bytecode, never debug source or serialized AST.

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

## Rust baseline and migration

The old implementation is preserved, including its standard sources and tests:

```sh
cargo build --release --manifest-path legacy/Cargo.toml
cargo run --manifest-path legacy/Cargo.toml -- run old-script.msync
```

Use `msync legacy ...` with a separately installed `msync-legacy` executable on
PATH, or set `MSYNC_LEGACY` to the baseline executable. Explicit old grammar
headers/`--syntax` selections and old `.mobj`/JSON artifacts dispatch to that
baseline or report the build command; their meaning is never guessed from debug
source. New scripts use `fn`, `let`, brace blocks and semicolons instead of
`symbol`/`boolean`/`macro`, indentation, `:=`, and `=>` bindings. `async`, `await`,
and `delete` are not language keywords; deletion is an ordinary native function.

Architecture, language, runtime, packaging, and measured validation live under
[`docs/design/`](docs/design). GPL-3.0-only; see [`LICENSE`](LICENSE).
