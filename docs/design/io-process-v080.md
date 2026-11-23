# File I/O and controlled Process values in 0.8

The standard library separates host operations from source composition. The
`io` module supplies file and standard-stream operations; its `read`, `lines`,
`bytes`, `write`, `append` and `copy` conveniences remain ordinary source
functions. Existing `fs` names and Path/String transfer behavior are unchanged.

## File and Range ownership

`read_text(path)` reads text using UTF-8 by default and recognizes a Unicode BOM.
`write_text(path, text)` and `append_text(path, text)` write UTF-8 without a BOM.
`read_lines` removes line separators without inventing a final empty line;
embedded NUL and Unicode scalars remain String data. `read_bytes` and byte
writers use Range<Number>, with the exact integral domain 0..255. This is a
semantic byte requirement, not an input-size restriction.

Line and byte readers open at enumeration, reopen on repeated enumeration and
close their stream when enumeration completes, is cancelled or is disposed.
`take(0)` does not open a local path or authenticate to a remote endpoint. The
interpreter's thin reader wrapper waits on selected overlapping path work before
each enumeration, rather than recording one observation when the Range adapter
was created. This matters when a stored Range is consumed, its file is replaced,
and it is consumed again. The Range source path is captured once on first demand;
later reads do not retarget it through a later process working directory.

Whole-file regular writes use the existing owned same-directory stage, one file
flush and atomic replacement. Invalid byte elements delete only that owned stage
and leave the prior target intact. Append modifies the selected file directly
and may leave a completed prefix after failure. File I/O follows symbolic links;
link-entry manipulation continues to use explicit `fs` link operations. Existing
special files are opened as streams rather than replaced with regular files;
they are not subjected to a regular-file fsync.

Remote reads and writes use one selected native SSH/WSL client channel for their
metadata/data operation. Reads preserve bytes on stdout rather than converting
through shell text. Writes feed stdin to `cat`, use a same-directory stage for
regular files, and retain the selected endpoint's grammar. The current remote
filesystem implementation follows the same POSIX/GNU utility contract as the
existing `fs` backend (`stat`, `readlink`, `sync`, `mv`). It never stores a file's
contents/existence as a compilation proof.

## Process ownership and controls

Ordinary `Path(arguments...)` retains inherited stdin. `process.spawn(path,
arguments)` is the host primitive for a live child with a controlled stdin pipe;
arguments are a Range of String/Path/Number/Bool and are staged exactly once.
Local execution uses `ProcessStartInfo.ArgumentList`, not a shell command string.

`ProcessValue` keeps its original three positional result fields and Completion
task. Optional control/PID properties add capabilities without serializing a
live handle into an object file. Spawn registers the same existing WorkOwner
and session process frontier as ordinary process calls. Native control methods
do not wait for the child before writing, closing, signalling or reading its
PID/running status. `wait` joins that process only; stdout/stderr/success/exit_code
retain their established deferred-completion behavior.

`kill` requests forcible termination (including the local process tree).
`terminate` requests SIGTERM on Unix or `CloseMainWindow` on Windows; console
applications on Windows can reject that request, returning false so the caller
may choose `kill`. Killing an already completed controlled process returns false.
The public [Process.Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill)
API documents why requesting a signal and waiting for exit are separate actions.

Closing stdin signals EOF. `communicate`, `run`, and `run_input` are normal source
compositions of spawn/write/close/wait. Function return still joins that
invocation's owned work: a source wrapper is not a hidden detached-process
facility. A live factory therefore uses the native spawn entry directly in the
desired owner. There is no process-global handle registry or polling timer.

## Single-channel remote process control

Controlled remote spawn uses one native client/channel throughout its lifetime.
A remote shell creates private input/start FIFOs, starts the child in a new
session when `setsid` is available, publishes the child's actual PID on the
control prelude of stderr, then lets the child exec its command. The start gate
prevents child stderr from racing ahead of that identity prelude. Subsequent
stderr is the child's normal byte stream.

The same stdin channel carries line-framed base64 input writes and CLOSE/TERM/
KILL requests. A background control reader forwards bytes to the child's FIFO;
it duplicates its input descriptor before entering the background job, because
POSIX shell asynchronous jobs otherwise see `/dev/null`. CLOSE closes only child
stdin, retaining the signal channel. No second authentication is needed to
terminate or inspect that controlled process. Group signalling is used when the
remote platform supplies `setsid`; otherwise the associated PID is signalled.
Normal exit and cancellation clean only the live owner's exact FIFO directory.

An ordinary remote Path call has no such control protocol. Its SSH/WSL transport
PID is not reported as the remote command PID. Requesting an unavailable identity
or a live control action gives an actionable error directing the caller to
controlled spawn; completed legacy result values remain valid for result access.

## Standard text streams in 0.8.6

`$std::io::read_line()` returns `String?`, removing the line terminator. An
empty line is `""`; only end of input is `null`. `read_stdin()` consumes the
remaining text without normalizing its line endings. `stdin_lines()` produces a
lazy `Range<String>` over the same input reader. Unlike a file-backed Range,
enumerating standard input again continues the shared stream, rather than
reopening or replaying it. Taking no lines does not read input. Early termination
releases the read turn and never closes standard input.

`print` and `println` write raw String text to stdout; `eprint` and `eprintln`
write to stderr. Line variants append the writer's line terminator; an embedded
newline in `print` is literal data. `flush` and `flush_error` explicitly flush
their channel. `prompt(text)` prints and flushes before reading one line. Use
`$std::text::format` explicitly for interpolation; braces in output data are not
treated as format syntax.

```msync
let io = $std::io;
let name? = io::prompt("Name: ");
if name is not null {
    io::println($std::text::format("Hello, {}!", name));
};
```

The API borrows a `TextReader` supplied through `ExecutionOptions.Input` and
the existing Output/Error writers. The init property preserves the positional
constructor. Overlapping standard-input reads share a turn keyed by reader
identity, without holding it across a yielded Range item. Injected streams
remain caller-owned. A program operation uses the reader's cancellation
contract; there is no abandoned background reader competing for the next line.

The CLI uses one lazily opened UTF-8 reader for redirected program input and
redirected REPL commands, so buffered read-ahead survives the transition back to
the language prompt. UTF-8 BOM is accepted; Windows textual execution selects
UTF-8 output at the existing saved/restored encoding boundary. LSP and binary
artifact output remain separate. `msync run -` already consumes stdin as its
source. There is no independently buffered raw-byte stdin reader: a second
reader (including a child inheriting the OS handle) cannot see bytes already
buffered by the first. Use a separate source file and explicit child input when
ownership must be transferred.

For redirected CLI input, line reads start the asynchronous stream reader
directly under the same reader turn. An already-buffered line completes
inline; when the buffer needs more bytes, the stream read supplies the pending
operation. The redirected reader uses an 8 KiB byte buffer to amortize
refills of piped or redirected input, without acquiring a second reader
or starting input before the program demands it. No per-line thread-pool
submission is required. The reader owns its
turn until the actual read finishes, even if a caller stops awaiting it.
An arbitrary injected reader (or the terminal reader) still starts on the
pool because its async method may block before returning.
`input.read_line` diagnostics record dispatch, turn contention and whether
the direct reader returned pending.

On the standard Set deduplication fixture (seed 17, same bound artifact),
seven alternating telemetry-off Native AOT pairs on CPUs 12–15 moved process
medians from 4.204 s to 3.094 s (paired median -26.0%, seven faster pairs).
A separate diagnostic run recorded 12,288 direct reads, 53 pending reader
results, and no waits for the reader turn.

TTY reads borrow the terminal only after the editor returns the submitted
statement. They reuse the existing cancellable foreground key input and the
plain-line editing shared with authentication prompts. Ctrl-C releases the
blocked read before the REPL accepts another statement; Ctrl-D/Ctrl-Z signal
end of input. The terminal's input encoding is not globally changed.

The design takes line/stream separation from [Rust `BufRead`](https://doc.rust-lang.org/std/io/trait.BufRead.html),
EOF and line records from [Java `BufferedReader`](https://docs.oracle.com/en/java/javase/24/docs/api/java.base/java/io/BufferedReader.html),
separate streams and useful prompt flushing from the [C++ standard stream contract](https://www.open-std.org/jtc1/sc22/wg21/docs/papers/2023/n4950.pdf),
and the actual [TextReader](https://learn.microsoft.com/dotnet/api/system.io.textreader.readlineasync?view=net-10.0)
and [TextWriter](https://learn.microsoft.com/dotnet/api/system.io.textwriter?view=net-10.0)
interfaces for host injection. `.NET Console.In` does not provide asynchronously
cancellable terminal line reads, so CLI foreground ownership is explicit.

Standard streams are native external effects, not filesystem paths or planner
constants. A prepared executable retains the operations, not a live reader or
writer. `stdlib_stdio_journey.py` checks an empty open pipe during check, plan,
and binding; it then drives real source/portable/executable runs, file and pipe
redirection, prompt flush, and redirected REPL ownership. Its `--tty` route
exercises the foreground handoff and Ctrl-C on PTY/ConPTY. The focused
`Msync.StdlibIOProcess.Tests --standard-streams` host also checks injected
reader ownership, writer line endings, explicit flush and cancellation.

## Reproduction and acceptance

`tests/StdlibIOProcess` contains a cross-platform child apphost and the focused
runtime/source/codec checks. The actual product-CLI runner is:

```text
python3 tests/StdlibIOProcess/native_journey.py --binary MSYNC --child CHILD
```

It executes source, `.mso` and `.mexe` for actual child identity, stdin EOF,
kill/terminate, repeated line reads after queued writes and Range-byte roundtrip.
`remote_native_journey.py` uses the same interface over an owned loopback OpenSSH
fixture and checks exact client/authentication/session counts. Its `--case`
selector permits only the changed contract to be rerun. The separate
`remote_journey.py --binary TEST_APPHOST` exercises the managed domain assembly;
its result is deliberately not represented as Native AOT product evidence.

The first real SSH domain acceptance observed 17 operations, 17 native ssh
clients/authentications/sessions, no scp processes, and correct text/NUL/all-byte
payloads. Nine controlled process runs independently compared the reported PID
to the child application's own PID, so a positive proxy PID could not pass.

## Diagnostic timelines at selected physical boundaries

The opt-in `diagnostics` level adds operation intervals without changing
transfer or process ownership. Physical I/O records only work actually
selected: residual and batch transfers feed local item spans; staged regular
files report writes/copies, actual publication moves, and separate explicit
file syncs where the implementation performs them. The file byte metric is
the output stream position after successful copying, not file metadata size;
the Windows native-copy path reports its source size separately. Directory
merges and archive installations retain their existing semantics. Observing
them does not add a flush, filesystem scan, or SSH command.

Native transport startup and exit or owned-session close are correlated by
the transport PID. Selected query, selector, metadata, send, download, and
protocol response intervals carry the actual endpoint and operation
dimensions. Archive byte counts are payload, not encrypted wire bytes; native
client startup is not an estimate of SSH authentication time. A logical
endpoint can share a physical tunnel, as described in
[the SSH connection protocol](https://www.rfc-editor.org/rfc/rfc4254.html).

Process startup and collection preserve parentage across asynchronous flows
and identify local transport vs. controlled process PIDs. Completion tasks
carry the producer span for dependency joins. Exit wait and pipe collection
can overlap; stdout and stderr provide byte counts, not argument vectors or
payloads. Parent intervals and concurrent child spans are inclusive and must
not be summed as exclusive CPU. This follows the distinction between
[asynchronous context](https://learn.microsoft.com/en-us/dotnet/api/system.threading.asynclocal-1?view=net-10.0)
and [causal links](https://opentelemetry.io/docs/specs/otel/trace/api/#add-link)
between separately owned work.

The focused loopback check is
`python3 tests/Telemetry/ssh_diagnostics.py /path/to/msync`. Its runtime
condition separates selecting a real transfer from merely mentioning a Path
in an unselected branch; it checks source, object and executable routes,
native client count, matched transport start/end identity, local bytes and
atomic publication ancestry directly from SQLite.
The same fixture checks a prepared two-file upload, including archive
payload counts and the shared batch-response owner. On Linux Native AOT
image `316c3310173b2ba07155a9a4bcd40a4c593771a0882aa13522d5cfd52f105214`,
all four real SSH cases passed: each launched one client with the original
configured alias. The raw trace databases are under
`.temp/ssh-diagnostics/s-5e4ke5tn/work/`. This is not Windows or WSL evidence.
