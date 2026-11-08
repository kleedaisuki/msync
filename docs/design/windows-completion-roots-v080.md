# Windows roots during path completion

Completion has three distinct inputs: source spelling, the physical lookup path,
and the captured working directory. Resolving the lookup path must not rewrite
the user's source or introduce a `$local` qualification.

## Root-relative and UNC input

The [Windows path-format contract](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats)
distinguishes a rooted path from a fully qualified path. In particular, one
leading separator selects the root of the current volume, whereas a UNC volume
contains both a server and a share.

| Input/query directory | Meaning for completion |
| --- | --- |
| `\` or `/` | Resolve against the captured working volume, then enumerate that root |
| `\\` | An unfinished UNC authority; there is no directory to enumerate yet |
| `\\server` or `\\server\` | No share has been supplied; do not pass this incomplete directory to `DirectoryInfo` |
| `\\server\share\` | A real UNC root; enumerate normally and preserve native errors |
| `D:\` or `D:/` | Explicit drive root; retain the source's separator spelling |
| `D:` | Drive-relative, not an alternative spelling of `D:\` |
| `\\?\C:\` | A DOS device path, not a server/share UNC prefix |
| `\\?\UNC\server\share\` | Extended UNC syntax with its own server/share root |

Classify the **directory being queried**, not the candidate basename. While the
user types `\\server\share`, the normal parent directory is still
`\\server\`; listing shares is not ordinary filesystem-directory enumeration.
Once the share separator has been entered, its root can be queried normally.
The incomplete states need no network call and must not become an LSP internal
error. This is grammar-aware admission, not a blanket UNC rejection or a catch
that conceals permissions and unavailable-share failures.

## Captured context

[`Path.GetFullPath(path, basePath)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath?view=net-10.0)
provides deterministic Windows qualification against an explicit base. The
[runtime implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Path.Windows.cs)
uses the base volume for rooted-relative paths. Completion should use
`lookup.WorkingDirectory ?? workingDirectory` when an accepted Path supplies a
captured context, including after a REPL directory change.

At investigation time, `PathValue.LocalPath` returned every `IsPathRooted` path
unchanged. Thus a Windows root-relative `\dir` could escape its captured drive
when later I/O used a different process drive. This should be addressed at the
shared local-projection boundary rather than only in a completion-specific
wrapper. Do not generalize the correction into POSIX lexical normalization:
normalizing `symlink/..` before kernel traversal can change its meaning.

## Focused native checks

The meaningful Windows journey is one separator, two separators, an unfinished
UNC server, an unfinished share, an explicit drive root, and a real complete
share if the test host supplies an owned SMB fixture. Root-relative checks use a
captured base distinct from process cwd. The response must preserve source
prefixes and avoid synthetic endpoints; completed ordinary paths still expose
real lookup errors. These are expected contracts, not a claim of native Windows
execution on a Linux investigation host.
