# Native capability journeys

Run from the repository root:

```console
dotnet run --project tests/Msync.Native.Tests/Msync.Native.Tests.csproj --artifacts-path .temp/build-natives -m:1 -nr:false
```

The harness uses actual local files and, on Unix, a native POSIX shell behind an
SSH-argv fixture. It does not require or claim an authenticated SSH server.
Fixtures are owned under `.temp` and removed on completion. The journey checks
copy/mutation, hard-link identity, dangling symlinks, FIFO classification,
permissions, Unicode/backslash/newline path preservation, lazy ranges and
short-circuit callbacks. Remote lexical operations use POSIX path semantics on
all hosts and retain endpoint identity without invoking transport.

## Porting findings

- `File.Exists` can report true for a dangling symlink. Unix `fs.exists` and
  status therefore use one `stat`/`lstat` call with the native ABI; only ENOENT and
  ENOTDIR become not-found, while other errors preserve the native cause.
- Shell command substitution discards trailing newlines. Remote path captures
  append/remove a sentinel so an ordinary newline filename or symlink target
  remains data rather than being truncated.
- Remote metadata uses GNU `stat`/`find`/`realpath`, matching the Linux endpoint
  transport. Its C-locale not-found diagnostic distinguishes absence from access
  errors. Metadata and publication errors remain visible through native stderr.
- Lazy `IAsyncEnumerable<Value>` factories defer directory enumeration and
  callback invocation. `take(0)` does not invoke a missing source directory;
  `front`/`any_of` do not consume unneeded items. See Microsoft's
  [async stream cancellation contract](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/generate-consume-asynchronous-stream).
- Pure lexical path operations follow the existing C++ filesystem-compatible
  model in `legacy/src/execution/native_fs_path.rs` rather than `GetFullPath`,
  which would resolve lexical operations against the host working directory.

The initial integration passes 86 assertions. It also checks the legacy status
labels (`regular`, `block`, `character`, `unknown`), octal/aggregate permission
flags, String-as-filename native copy arguments and zero-argument `ranges.of()`. Native Windows filesystem API
bindings compile with the same project; actual Windows execution is a separate
platform acceptance responsibility.
