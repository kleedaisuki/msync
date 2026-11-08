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

## Actual native Windows editing baseline

[The native editing run](https://github.com/kleedaisuki/msync/actions/runs/42506951530)
used committed source `2b434542e0bd0cc836df07a73386949de36ef41f` on Windows
Server 2025. Its executable SHA-256 is
`f255f77212e6437503b4358e13a73f1b2ff30c88910ae91bd515f08f22925502`.
All seven unfinished/root input cases returned normally, including one separator,
two separators, incomplete server/share prefixes, `C:`, `C:\` and `D:/`.
The captured-volume projection program (`--windows-rooted`) also passed on that
actual host. The same native run passed the real upstream document-symbol
converter, minimal leaf edits, builtins, lexical navigation, canonical range
execution and the narrow ConPTY default-palette journey.

The sole Windows failure was in the identifier/return **fixture**: writing the
formatter's LF output with Python text mode converted it to CRLF before checking
canonical formatting. The fixture now writes those exact UTF-8 bytes.
[The identifier-only follow-up](https://github.com/kleedaisuki/msync/actions/runs/42508511731)
reused the same executable and native dependencies without another AOT build;
all nine byte-result programs, three return diagnostics and seven editing cases
passed. Its report records native source `2b43454` separately from fixture source
`0c55e60924067af67cbe16ba9259e046ab51d0c5`. The accepted Linux editing image is
SHA-256 `4a0a861a3656f69320845e25c72407c1c884c6765df670c76eead58900a05fc8`.
These images are the working editing baseline, not acceptance of the subsequent
mutable-state/optimizer/scheduler implementation.
