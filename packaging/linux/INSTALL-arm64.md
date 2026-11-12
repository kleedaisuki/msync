# msync for Linux ARM64

Extract this GNU/Linux AArch64 archive and run `./bin/msync --help` or
`./bin/msync` for the REPL. The executable is Native AOT and needs no
installed .NET runtime or system ICU. Use a glibc system compatible with
the build host/sysroot. If the binary needs a newer system library, rebuild
the package against the deployment system.

Keep the extracted package in a stable directory, for example
`~/.local/opt/msync-0.8.6-linux-aarch64`. Keep `libe_sqlite3.so` beside
`bin/msync` so telemetry can be enabled later. To add the command to PATH:

```sh
mkdir -p ~/.local/bin
ln -s ~/.local/opt/msync-0.8.6-linux-aarch64/bin/msync ~/.local/bin/msync
```

Ensure `~/.local/bin` is on PATH. To upgrade, extract the new package and
update the symlink. To uninstall, remove the symlink and its extracted
package, keeping your scripts, configuration and data.

Use `msync plan FILE` or `msync run FILE --dry-run` to inspect transfers
before execution. Remote SSH uses the system OpenSSH client; local
copying does not need an SSH service. The CLI can use its numeric JIT
where the Linux kernel and libgcc cache helper support code publication;
the interpreter remains available with `MSYNC_JIT=off` or when native
publication is unavailable.

Check the archive against its accompanying SHA-256 checksum before
extracting. A checksum detects corruption but does not authenticate the
publisher.
