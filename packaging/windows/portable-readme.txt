# msync for Windows

Portable package: extract the complete ZIP, then run bin\msync.exe --help.
The ZIP changes no PATH, registry, user configuration or SSH accounts. Add its
bin directory to your user PATH yourself if desired; keep e_sqlite3.dll beside
msync.exe and retain the remaining documentation and license information. Do not
copy only the executable away from its native library payload.

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

msync reads %APPDATA%\msync\config.toml, with ~/.msync/config.toml as the fallback;
--config selects an explicit file, --no-config disables discovery, and msync config
prints effective settings. It does not save SSH passwords. Existing OpenSSH
configuration remains authoritative (~/.ssh/config, or MSYNC_SSH_CONFIG).
Remote transfers require the native ssh client; a finite transfer uses one
native channel, not a separate scp authentication. WSL endpoints use the optional
Windows WSL feature. Local copies do not need either transport.
No OS feature, SSH server, account, runtime redistributable or administrator
component is installed automatically.

The supplied executable uses .NET 10 Native AOT and needs no installed .NET
runtime or ICU package. The complete native SQLite payload ships in bin.
manifest.sha256 lists SHA-256 hashes of all other portable payload files;
SHA256SUMS on the official GitHub Release covers the downloaded ZIP and installer.
These hashes detect corruption,
not publisher identity: an unsigned installer may trigger Windows SmartScreen.

Building packages (not using them) requires a Git checkout, the .NET 10 SDK,
Visual Studio's native C++ toolchain and Windows SDK, and Python 3. Inno Setup 6
is needed to build the installer; it is not needed for the portable ZIP.
Complete project sources are available at https://github.com/kleedaisuki/msync;
use the source archive for the same version tag as the distributed executable.
