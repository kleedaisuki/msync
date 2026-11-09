# File I/O and controlled Process values in 0.8

The standard library separates host operations from source composition. The
`io` module adds eight host operations; its `read`, `lines`, `bytes`, `write`,
`append` and `copy` conveniences remain ordinary source functions. Existing
`fs` names and Path/String transfer behavior are unchanged.

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
