# msync for Linux x86-64

Extract the archive, then run `./bin/msync --help` or `./bin/msync` for the REPL.
The release workflow builds GNU/Linux packages on Ubuntu 22.04 (glibc 2.35).
Other architectures and older libc systems should build from the matching
source tag. The published executable is Native AOT and needs no installed .NET
runtime or system ICU installation. String case mapping and regex operations
use the language's invariant/ordinal semantics; the runtime does not expose
locale-dependent operations.

Keep the extracted package in a stable per-user directory, for example
`~/.local/opt/msync-0.8.6-linux-x86_64`. Keep the complete `bin/` payload together:
`libe_sqlite3.so` is a runtime dependency and must remain beside `msync`, including
when telemetry is enabled later. Copying only the executable loses that library.
Create a command symlink and ensure `~/.local/bin` is on PATH:

```sh
mkdir -p ~/.local/bin
ln -s ~/.local/opt/msync-0.8.6-linux-x86_64/bin/msync ~/.local/bin/msync
```

No root permission or automatic configuration change is necessary. To upgrade,
extract the new complete package and update the symlink. To uninstall, remove
the symlink and its extracted package directory, preserving your scripts,
configuration and data.

Review a script with `msync plan FILE` or `msync run FILE --dry-run` before real
transfers. The program does not delete unmentioned files or install an SSH service.
Remote SSH operations use the system OpenSSH client; native aliases, agents and
credentials remain managed by OpenSSH. Local copying does not need an SSH
service. WSL is an optional endpoint on a native Windows host, not a Linux
installation prerequisite.

Verify the downloaded archive against SHA256SUMS from the same official GitHub
release. Checksums detect corruption; they are not a code-signing identity.
Source and license are available from the exact release tag on the
[release page](https://github.com/kleedaisuki/msync/releases).
