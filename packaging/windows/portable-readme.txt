# msync for Windows

Portable package: extract the complete ZIP, then run bin\msync.exe --help.
The ZIP changes no PATH, registry, user configuration or SSH accounts. Add its
bin directory to your user PATH yourself if desired; keep the remaining files
for documentation and license information.

Installer: run msync-VERSION-windows-x86_64-setup.exe. Installation is per-user,
requires no administrator privileges, and defaults to %USERPROFILE%\.msync.
The optional Add to PATH task adds only the installed bin directory to your user
PATH. Restart an already-running terminal application (not just a new tab), or
open msync REPL from the Start Menu immediately. Upgrades replace managed program
files; uninstall keeps user-created configuration and removes only PATH entries
owned by the installer, not a matching entry you previously added yourself.

First safe journey:
  msync --version
  msync check sync.msync
  msync run sync.msync --dry-run
Inspect the preview before running without --dry-run: execution writes real data.
See share\doc\msync\README.md for language, IR commands and resource controls.

msync does not yet read ~/.msync/config.toml or save SSH passwords. Existing
OpenSSH configuration remains authoritative (~/.ssh/config, or the explicitly
selected MSYNC_SSH_CONFIG). SSH/WSL/HTTP features require their native clients:
ssh/scp, WSL and curl respectively. Local copies do not need those optional tools.
No OS feature, SSH server, account, runtime redistributable or administrator
component is installed automatically.

The supplied executable uses Rust's MSVC target with a static C runtime.
manifest.sha256 lists SHA-256 hashes of all other portable payload files;
SHA256SUMS on the official GitHub Release covers the downloaded ZIP and installer.
These hashes detect corruption,
not publisher identity: an unsigned installer may trigger Windows SmartScreen.

Building packages (not using them) requires a Git checkout, Rust's MSVC toolchain,
Inno Setup 6 and Python 3 for collecting THIRD-PARTY-NOTICES.txt from the locked
dependency sources and matching Rust toolchain. None is needed for installation.
Complete project sources are available at https://github.com/kleedaisuki/msync;
use the source archive for the same version tag as the distributed executable.
