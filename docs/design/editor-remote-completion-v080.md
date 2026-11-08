# Retained native directory queries for editor completion

A selected SSH or WSL path completion queries the selected endpoint's real
filesystem. It is not a local projection of that endpoint, and it does not infer
remote contents from source text. Merely compiling, checking or opening a
Connection value does not create this session.

`RemoteDirectoryCompletion` owns one `Session` for each admitted Connection.
The owner is the editor or interactive prompt, not a process-global cache.
Each session owns its native process, redirected streams, credential broker and
serial query gate. `TransferEngine.CreateRemoteShellProcessStartInfo` reuses the
existing native transport builder: SSH aliases, `MSYNC_SSH_CONFIG`, URI ports,
native agent configuration and explicit WSL distributions retain execution's
existing meanings. The selected process executes a retained `sh -s` channel.

## Protocol and path meanings

The client defines a small POSIX shell function once and then sends quoted
function calls through the same standard input. Only owned program fragments
have their source line endings normalized. A selected path is a single shell
argument; single quotes, actual newlines and CRLF in that argument are retained.
The shell expands `~` from its own HOME and relative paths from its own PWD.
Consequently WSL's actual current directory and an SSH login's current directory
remain distinct from the editor's Windows or Linux current directory.

Responses are NUL-delimited records with separate type prefixes:

- `D` followed by a basename identifies a directory, including directory links.
- `F` followed by a basename identifies another entry, including dangling links.
- `X` identifies a failed query; native stderr retains the actual cause.
- `E` ends the query.

Names never become protocol delimiters: a filename containing a newline, a
quote, or the text `E` remains inside its typed record. The shell's three normal
and hidden-entry glob families enumerate ordinary directory names without an
external helper, temporary result files, a Python runtime, or a platform-size
limit. An unreadable directory is a failed operation, not an empty listing.
The retained producer remains alive while the receiver consumes its records;
this also avoids the early-exit stdout lifetime issue documented for native
Windows WSL archive producers.

The native command contract follows [OpenSSH's command and argument semantics](https://man.openbsd.org/ssh.1).
The runtime remains responsible for its native transport argument construction;
completion does not maintain a second SSH configuration evaluator for execution.
Local host-name suggestions are a separate catalog operation.

## Cancellation and failure ownership

Queries are serial within an endpoint, independent between endpoints. A request
cancelled while waiting for the gate never retires another request's active
channel. Once admitted, its cancellation retires only that owned native process,
its child prompt helper and credential broker. EOF, process-start errors and
failed directory operations retire the same owner. A subsequent request may
establish a new native session normally; there is no persistent recovery state
or stale directory result that must be accepted first.

Disposing the editor retires every session it owns and waits for stream readers
and brokers to finish. There are no global native-client kills, SSH control
resource scans, or unrelated transfer joins.

## Focused implementation evidence

The local encrypted OpenSSH fixture exercised three successful queries over
one authentication, one missing-directory failure with the native `cd` cause,
and one successful retry over a new authentication: five queries, two native
clients, two authentications. The queried directory itself contained a single
quote and newline. Its names covered hidden files, spaces, newlines, a single
quote, directories, a directory symlink and a dangling symlink.

A second selected endpoint blocked at an owned ProxyCommand admission marker.
Cancelling a queued request preserved the admitted owner and proxy; cancelling
the admitted request retired both native processes. This added one native
client and no authentication. The final record is
`.temp/remote-completion-ssh/s-gb9bfdan/result.json`.

The portable editor journeys and the native Windows WSL fixture own integrated
completion acceptance. Their results should be attached to this protocol
contract rather than substituted with a local fake transport.
