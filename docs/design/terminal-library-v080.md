# Typed terminal tools and interactive context (v0.8.0)

The terminal library is executable source in `stdlib/shell.msync`. It expresses
common terminal operations using the existing Path, String, Range, Process and
Connection semantics. It does not assemble a shell command line or introduce a
second command grammar. Public function names use `snake_case`.
The self-contained [terminal-tools example](../../examples/terminal_tools.msync)
filters an actual log, creates a report and copies the selected output.

The useful production precedent is Nushell's separation between returned data
and terminal rendering ([Thinking in Nu](https://www.nushell.sh/book/thinking_in_nu.html)).
msync uses its existing compiler, mutable lexical locations and endpoint-aware
paths for that separation; it does not adopt another shell's syntax or runtime.

## Composition rather than terminal-output parsing

`$std::shell` is a source-backed named namespace. Directory tools (`ls`,
`ls_at`, `find`, `find_if`) produce `Range<Path>`; text filters (`lines`, `head`,
`tail`, `grep`, `grep_fixed`, `grep_not`, `sort`, `uniq`, `sed`, `cut`) produce
`Range<String>`. These results compose through ordinary calls and Range
operators. `nl` produces `{ index: Number, value: String }` rows, `wc` returns
named counts, and `ps` returns `{ pid: Number, name: String }` rows rather than
requiring callers to parse an OS-specific column layout.

```msync
let shell = $std::shell;
let selected = shell::head(shell::grep("error", ./application.log), 20);
$std::io::write_text(./errors.txt, $std::text::join_lines(selected));

let remote = ssh://forge;
let remote_files = shell::find(remote @ ~/reports);
let text_files = $std::ranges::filter(remote_files, fn(path: Path) {
    shell::basename(path) as String == "summary.txt"
});
text_files -> ./downloads;
```

A Path input to a text filter denotes a file; String input denotes text;
`Range<String>` input denotes existing logical lines. `cat` always takes a path
(a String there is a filename). `cp` similarly treats its String source as a
filename, deliberately differing from the language operator `String -> Path`,
which writes text. Transfer implementation and selected endpoint ownership are
otherwise the existing language operations: finite Range copying remains one
transfer, ordinary files retain atomic replacement, and no rollback journal is
introduced.

`uniq` removes adjacent equal lines without sorting, as its Unix namesake does.
`sort` is explicit and uses the language's ordinal text ordering, not the host
shell locale. `tail` retains at most the requested number of lines and does
not materialize the complete input first. Zero demand does not read its source.
Each enumeration owns its iterator state; a second enumeration does not inherit
the previous call's last line. Count admission uses the same finite integral
Int64 semantics as other Range operations, without an artificial Int32 quota.

`wc` counts newline characters, whitespace-separated words, Unicode scalars and
UTF-8 bytes. For Path input the byte count is the actual file size, including a
byte-order mark; text counts use the IO library's decoded String semantics.
`wc_lines` instead counts logical line records. These differ for a
last line lacking a newline, intentionally. `cut` uses zero-based whitespace
field indexes; `sed` performs regular-expression replacement on each line.
These typed interfaces are not a reproduction of every Unix command-line flag.

Filesystem mutation includes `cp`, `mv`, `rm`, `rmdir`, `rm_tree`, `mkdir`,
`mkdir_one`, `touch`, `ln`, `ln_s`, and `chmod`. `rm` does not recursively remove
directories; `rmdir` never deletes a regular file or a symbolic link. `touch`
creates an absent file or updates access/modification times without truncating
existing content. Its selected remote implementation uses the existing native
SSH operation channel. Unix numeric or named permission modes follow the
filesystem library, including its Windows read-only mapping.

Environment operations reuse the process environment API. Process operations
retain actual Process values and controls; `kill` and `terminate` consume a
Process rather than interpreting an arbitrary Number as a local PID. `which`
returns an executable Path or Null, using PATH/PATHEXT and actual Unix execute
access. Process enumeration reports an inaccessible name explicitly instead of
silently removing an otherwise observable process row.

## REPL bindings and selected values

The REPL imports the shell module's named exports once through an ordinary
compiler-session submission. The aliases are normal mutable, shadowable
bindings; there is no runtime command-name registry. A program file does not
receive these implicit globals. `ls()` selects the execution directory;
`ls_at(path)` selects another directory. There are no implicit optional
arguments or bare POSIX command syntax.

```msync
pwd()
cd(./work)
ls()
head(grep("error", ./application.log), 20)
```

Top-level Range results are streamed to the terminal with cancellation rather
than displayed as the placeholder `Range<T>`. No arbitrary row limit is applied.
Scope results display their actual fields. A root Null remains silent, while
Null items inside a selected Range are visible. Accepting the compiler-session
submission after result enumeration retains mutations performed by selected
callbacks. Terminal completion resolves paths against the same session directory.

### Windows console Unicode boundary

The Windows Native image from run
[42646810447](https://github.com/kleedaisuki/msync/actions/runs/42646810447),
SHA-256 `3d35993771224559e08405c9454f01a51fd7912f660b7932504e784d5dd43415`,
preserved Unicode input but lost characters while painting through the inherited
console output code page. The immutable-image
[A/B diagnostic](https://github.com/kleedaisuki/msync/actions/runs/42649863293)
measured six UTF-8 bytes for `空间` and successfully entered/read the Unicode
directory in both cases. Only a CP65001 console painted the original characters;
the default console painted `??`. The diagnostic's ASCII suffix boundary is
not used by the acceptance fixture, which still requires complete Unicode paint.

`ConsoleEncodingScope` selects UTF-8 without a BOM once, only when Windows
stdout or stderr targets a console. It restores the inherited encoding on exit because the console
code page belongs to the shared terminal. No input encoding, argument spelling,
path provenance or per-key logic changes. Fully redirected output and the
language server do not enter this scope. With mixed redirection, the shared
encoding also preserves diagnostic text; raw artifact/protocol byte streams
remain owned by their existing writers.

This follows the actual
[.NET Windows console implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Console/src/System/ConsolePal.Windows.cs)
and Microsoft's
[Unicode/CP65001 console guidance](https://learn.microsoft.com/en-us/windows/console/console-code-pages).
The focused fixture remains `repl_shell_journey.py`, including Unicode/spaced
directory completion, a gated Process across submissions and streaming Ctrl+C.

Final Native acceptance in
[42654041790](https://github.com/kleedaisuki/msync/actions/runs/42654041790)
passed that unchanged strict fixture on both Linux and Windows. The Windows
image SHA-256 was
`673a0f863b534c0bf1d7758069ee82c9c113bf5bbf54fc9e1c4d323fdf9861c4`;
the phase exited successfully with empty stderr and verified the original
Unicode paint, session-directory completion, live/completed Process states and
prompt recovery after streaming cancellation. The diagnostic CP65001 wrapper
was not used for this acceptance run.

## Directory ownership and deferred admissions

`environment.set_working_directory` and `shell.cd` change an execution's local
context. `ExecutionSession` retains that directory for future submissions;
neither the function nor `/cd` changes `Environment.CurrentDirectory`. Physical
remote operations retain explicit `connection @ path` selection rather than
pretending that a remote directory is the host process's directory.

Existing Path values keep their captured origin. Future submissions capture
their newly declared paths against the updated directory. Already-selected
deferred path conversions, IO and transfers also retain their admission origin,
even if a Process yields the final path text only after `cd`.

The interpreter uses a lazy per-directory-epoch context capture for deferred
work. The capture copies immutable projection/configuration fields and shares
the existing output/observation callbacks; it owns no transport, credential,
process or task resources. It is allocated only when deferred/IO work needs it,
not on arithmetic/scalar paths. A directory change invalidates the current
capture without changing earlier captures or joining unrelated jobs.

Deferred scalar dispatch examines the actual argument kinds rather than a
native declaration's potentially broad `Any` parameter. In particular, casting
a deferred Process output String to Path must not await that process before the
caller can close its stdin. Real Range/Function operands retain their existing
demand/callback handling.

## Validation entrypoints

- `tests/Msync.EndToEnd/terminal_library_journey.py`: actual filesystem/text,
  process/environment and content-preserving touch workflows through source,
  source-deleted `.mso`, and bound `.mexe` launched from another directory.
- `tests/Msync.EndToEnd/repl_shell_journey.py`: direct aliases, persistent
  directory/capture semantics, streamed values, errors, real terminal completion
  and cancellation. It is separately selectable for native platform follow-ups.

The focused local library journey accepted nine O0/O2/O3 source/object/executable
routes with 48 exact output files per route. Two stdin-gated child processes
kept their final filename text unavailable until after `cd`; both an implicit
transfer destination and an explicit `as Path` retained the earlier admission
directory, without making `cd` join the waiting processes. Separate edge checks
covered repeated `uniq` traversal, lexical basename/dirname behavior, invalid
counts, file-preserving `rmdir` rejection, and execute-only versus non-searchable
Unix directories. Native platform closure is owned by the delivery record rather
than inferred from these managed observations.

The backend uses the platform file timestamp API rather than rewriting data;
see [.NET File.SetLastWriteTimeUtc](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.setlastwritetimeutc?view=net-10.0).
The distinction between newline counts and logical records follows the
[GNU Coreutils counting contract](https://www.gnu.org/software/coreutils/manual/html_node/wc-invocation.html).

## Typed HIR persistent sessions (0.8.1)

`CompilerSession` owns one serialized semantic workspace and one independent
`HirLoweringSession`. Accepted bindings retain logical location identities;
shadowing allocates another identity. Each source submission receives a new
source identity while the session entry function remains stable. Binding imports
previous bound declarations and generic bodies, not previous source or AST
replay. Lowering receipts translate logical locations, callables and allocation
sites to runtime slots and code identities.

A source-generation transaction journals replacements of existing HIR records
and checkpoints appended tables, including type interning. A successful compile
keeps its pending generation; a failed compile restores the accepted workspace.
Editor analysis freezes a read-only observation projection and then discards its
temporary generation. This ownership transaction is distinct from generic
inference queries, which have no executable-body writer. Cached prompt
information contains type/signature/source projections, not typed body arenas.

Execution acceptance projects actual exported values into semantic facts through
the lowering receipt. Scope aliases and captured locations retain identity;
recursive closure cells are resolved after their reachable descriptors exist.
Only immutable scalar/path/connection descriptions and type/callable metadata
cross this boundary. Processes, credentials, lazy Range producers and live
runtime objects are neither traversed for effects nor embedded in HIR. Partial
runtime failure retains initialized exported locations, including surviving
Processes, rather than publishing declarations whose initializer failed.

The REPL refreshes this feedback after every completed or failed execution and
uses the accepted facts while editing the next submission. An explicit
`AnalyzeEditor` runtime resolver remains supported: it refreshes a temporary
facts overlay without changing the accepted generation. This avoids redundant
exported-object traversal on every normal prompt keystroke while preserving
live-feedback queries for other consumers. Existing Paths retain their captured
origin across `cd`; the host process working directory is never changed.

Completed generic function instances are retained in a session code cache keyed
by semantic types, callable definitions and input topology. Their summaries use
formal locations and regions, not an earlier caller's runtime resources. A
source-generation addition lease commits newly completed promises only after
lowering and optimization succeed; rejected compilation and editor generations
drop their additions alongside the HIR arena rollback. Generic inference
workspaces have independent caches. Repeated submissions therefore reuse the
same typed body without copying accepted semantic state or allocating another
function identity for each call.

Persistent compilation runs the existing optimizer after lowering, preserving
runtime-local and callable layout receipts. The established `PreserveLocals`
policy leaves the session entry intact while optimizing retained non-entry
functions at the selected level. Cache reuse does not independently renumber
constants or callable indices: the backend still publishes one coherent image.
