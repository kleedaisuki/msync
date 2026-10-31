# Control-flow filesystem operations

The typed control interpreter delegates existence, deletion, and regex-range
admission to `src/execution/control_fs.rs`. The helper reuses the existing
contextual path resolver and cancellable, bounded SSH/WSL transport owner rather
than launching an independent shell transport implementation.

## Semantics and support

| Operation | Native/ENV | PATH | WSL/SSH | HTTP/TCP |
| --- | --- | --- | --- | --- |
| `exists` | Follow-target metadata | Search regular filename | Follow-target GNU `stat` | Explicit unsupported error |
| `delete` | Unlink file or symlink | Readonly error | Nonrecursive `rm --` | Explicit unsupported error |
| Regex range | Sorted UTF-8 entry names | Not a directory | NUL-framed sorted UTF-8 entry names | Explicit unsupported error |

Existence returns false only for a missing target; a dangling symlink is false,
but a symlink cycle or metadata access failure is an error. Directories exist.
Deletion inspects the link itself and does not dereference its final component.
Actual directories and special entries are rejected; the operation never enables
recursive removal. Missing deletion targets fail rather than silently succeeding.
Both pathname inspection and unlink can race with unmanaged writers: the contract
is not an identity-pinned filesystem sandbox. In particular, prior `exists` is
not an authorization or a promise that a later transfer can still open the path.

Remote existence cannot use shell `test -e` alone: that conflates inaccessible,
cyclic, and absent paths. The helper uses GNU `stat -L` with `LC_ALL=C`, recognizing
only the final ENOENT diagnostic suffix as absence. Other diagnostics are retained
with endpoint/path context. Existing remote publication already assumes GNU tools.
Every user path is a positional argument through the shared transport quoting
owner, not interpolated into shell source. Transport stdout is bounded at 8MiB.

Ranges match the entire entry name, not a substring. They reject non-UTF-8 names
rather than silently losing entries. Directory name acquisition may buffer names
for deterministic lexical ordering; it does not inspect symlink targets or run
filters. Predicate and consumer laziness remains the interpreter's responsibility.

## References

- [Rust `metadata`](https://doc.rust-lang.org/std/fs/fn.metadata.html) follows links;
  [`symlink_metadata`](https://doc.rust-lang.org/std/fs/fn.symlink_metadata.html)
  reports the link entry instead.
- [Rust `remove_file`](https://doc.rust-lang.org/std/fs/fn.remove_file.html)
  removes a symlink rather than its target; deletion can fail for permissions or
  concurrent pathname changes.
- [GNU `stat`](https://www.gnu.org/software/coreutils/manual/html_node/stat-invocation.html)
  documents `--dereference` / `-L`.

## Focused evidence

Six helper unit tests cover current-state existence/deletion, actual-directory
rejection, cancellation preserving the file, full-name Unicode ordering, link
unlinking/dangling/cyclic existence, and remote name framing. Their execution is
owned by the root's incremental Cargo window and is not implied by this note.

The actual remote shell body was extracted into `.temp/control-fs-v6/` and run
locally with `sh` on Linux. The journey passed regular/missing/dangling existence,
symlink-cycle error preservation, link deletion, real-directory deletion refusal,
and NUL-framed enumeration. This tests shell semantics, not SSH authentication,
Windows filesystem behavior, or a remote-host transport connection.
