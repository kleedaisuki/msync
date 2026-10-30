# msync for Linux x86-64

Extract the archive, then run `./bin/msync --help` or `./bin/msync` for the REPL.
The official release GNU/Linux binary is built on Ubuntu 22.04 (glibc 2.35); older libc systems and
other architectures should build from the matching source tag.

For a per-user command, copy `bin/msync` into `~/.local/bin` and ensure that
directory is in your PATH. No root permission or automatic configuration change
is necessary. Remove that executable to uninstall; keep your own scripts/data.

Review a script with `msync plan FILE` or `msync run FILE --dry-run` before real
transfers. The program does not delete unmentioned files or install an SSH service.
Optional transports use the system's curl/OpenSSH/WSL clients as documented in
the bundled README. Native SSH settings remain managed by OpenSSH.

Verify the downloaded archive against SHA256SUMS from the same official GitHub
release. Checksums detect corruption; they are not a code-signing identity.
Source and license are available from the exact release tag at
https://github.com/kleedaisuki/msync/releases .
