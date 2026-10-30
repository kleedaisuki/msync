# Windows distribution and installation

## Product contract

The first distribution uses two Windows x86-64 assets from the same MSVC release
build: a per-user Inno Setup installer and a portable ZIP. Installation needs no
administrator account and must not enable Windows capabilities, download clients,
change SSH authentication settings, or introduce an application-settings loader.
The installer defaults to `%USERPROFILE%\.msync`; its executable is
`bin\msync.exe`. The existing self-executable authentication helper remains part
of that executable, not a second installation or a password-bearing script.

The application home can also contain user-owned `config.toml`, data, scripts or
future auxiliary tools. Installation and uninstall own only explicitly packaged
files, their installer metadata, shortcuts and an optional PATH component.
Uninstall must not recursively delete the application home or infer ownership of
unknown files from their location. A possible future settings loader must not be
represented as already existing just because a conventional home is selected.

## Layout and interaction

```text
%USERPROFILE%\.msync\
  bin\msync.exe
  installer\unins000.exe + installer-owned metadata
  share\doc\msync\README.md, LICENSE, INSTALL.md, docs\...
  config.toml / data / other files   # user-owned, if present; never generated
```

Setup exposes an installation-directory choice and a clearly named per-user PATH
task. Its Start Menu entry launches the interactive REPL in the user's profile,
not inside a versioned package directory; documentation and uninstall are also
discoverable. No file association or automatic file-copy job is installed. A
finished installation should tell the user to open a **new** terminal for PATH
changes; an already-running shell has its own inherited environment.

The portable archive uses the same `bin` and documentation layout plus a root
`INSTALL.md` and payload manifest, without registration or PATH mutation. Extract it where desired and execute
`bin\msync.exe`, or explicitly add that bin directory to the user's PATH.
Do not extract an archive over an unknown application home and then claim every
file there is managed by the package.

## PATH and upgrade ownership

Only `HKCU\Environment\Path` is modified. Preserve its original registry type,
unrelated raw component spelling, order and variable references. Never copy the
combined process/Machine PATH into the User value, and never use `setx` (which is
not an ownership-aware edit). Compare directory components case-insensitively
with ordinary trailing-separator/quote normalization, but retain raw unrelated
components rather than rewriting the entire value into a preferred format.

Record whether this installation actually inserted its bin component. An already
present component is not installer-owned and must survive uninstall. Upgrades
retain ownership rather than mistaking their previous insertion for a user entry;
task changes and installation-directory changes must not leave a dead owned PATH
entry behind. Re-read the latest value before editing, serialize this installer's
edits, and notify Windows environment consumers. These measures do not pretend to
provide a transaction with arbitrary third-party PATH writers.

Keep a stable application identifier across versions so upgrades update one
per-user installation rather than silently registering parallel products. The
installer and portable package contain no automatic synchronization script, and
upgrading must preserve arbitrary application-home content and native SSH
configuration.

## Build and provenance

`packaging/windows/build.ps1` owns native release compilation, payload staging,
ZIP construction, Inno compilation and SHA-256 output. The package version is read
from `Cargo.toml`, not independently copied into installer source. MSVC builds
use `-C target-feature=+crt-static` so a portable extraction does not depend on the
GitHub runner's already-installed Visual C++ Redistributable. The existing
Windows system DLL requirements and optional native clients remain unchanged.

Disposable staging stays under project `.temp`; distributable artifacts belong
in the explicit output directory (normally ignored `dist/`). Compiler tooling is
an ordinary package-manager/toolchain dependency, not checked-in installer output.
The build uses an installed Inno compiler or an explicit compiler path; it must
not bootstrap tools by downloading and executing an unreviewed shell script.

The ZIP has sorted entries and normalized timestamps. Its payload manifest and
release checksums support integrity/provenance checks; they are not a claim that
an unsigned Inno installer is byte-identical across compiler versions or that
checksums alone establish publisher identity. Unless signing is separately
configured, explain the unsigned publisher/SmartScreen limitation rather than
suggesting that installation disables Windows security warnings.

Build-time notice collection uses Python 3.10+ (not an installed application
runtime dependency):

```sh
python3 -B packaging/licenses.py --target x86_64-pc-windows-msvc --output STAGE/share/doc/msync/THIRD-PARTY-NOTICES.txt
python3 -B -m unittest discover -s packaging -p test_licenses.py
```

The output file includes actual source-package license/notice texts and declared
SPDX, author, version and source metadata. Cargo metadata can include development
and nonlinked packages; this is explicitly a conservative notice superset, not a
linked-binary SBOM. Its sibling `rust-attribution/` directory preserves the exact
matching toolchain's `COPYRIGHT-library.html` and referenced `licenses/` files.
Both directories are packaged recursively before manifest hashes are computed.
A custom toolchain missing that report fails with an actionable diagnostic rather
than substituting a guessed stable-version copyright report.

The published `clipboard-win` 5.4.3 crate omits its license from its Cargo include
list. Its actual upstream LICENSE is retained as a version-specific supplemental
input in `packaging/licenses/`, with exact upstream commit URL and SHA-256 in
`packaging/license-sources.json`. The collector checks the checksum and rejects
unknown missing notices. No license text is fetched from a moving branch during
a release build; this is distribution hygiene, not a legal interpretation of
license compatibility.

`ssh.exe`, `scp.exe`, `curl.exe` and WSL are optional feature dependencies, not
bundled OS capabilities. A local-copy user does not need them. Remote POSIX
stream helpers still have their documented remote Python requirement. Setup does
not request elevation to install any of those components.

### Commands and compiler inputs

```powershell
pwsh packaging/windows/build.ps1 -OutputDirectory dist
# Or, after an equivalent static-CRT release build:
pwsh packaging/windows/build.ps1 -SkipBuild -OutputDirectory dist
pwsh packaging/windows/test.ps1 -Installer dist/msync-0.4.0-windows-x86_64-setup.exe
```

The build accepts `-Target x86_64-pc-windows-msvc` and `-IsccPath` for an explicitly
installed compiler. It passes `AppVersion`, `SourceDir`, `OutputDir` and
`OutputBaseName` as Inno preprocessor definitions. The fixed application identifier
is `{157D15D7-7899-4F94-9D69-3CCF769886E8}`; do not change it on ordinary upgrades.
The lifecycle test reads the expected version from the checked-out Cargo package.

## Native acceptance

`packaging/windows/test.ps1` exercises the **generated installer**, not only the
build output, in a custom disposable installation directory. It must refuse to
clobber an existing msync installation and restore the original User PATH and
its type on failure as well as success. Required journeys:

1. Install without elevation, with an explicit PATH-task choice; execute installed
   `--version`, `check`, a real copy and IR export/reuse, verifying output bytes.
2. Upgrade the same installation, preserve a user configuration/data sentinel,
   and keep exactly one installer-owned PATH component.
3. Uninstall via the installed uninstaller; preserve arbitrary user content and
   unrelated/preexisting PATH entries while removing only owned entries.
4. Repeat with a preexisting bin component and with PATH declined, verifying
   that uninstall does not remove preexisting data or claim unselected changes.
5. Inspect the portable ZIP/manifest and package runtime imports where available,
   rather than trusting a successful Rust build as proof of clean installation.

Run these journeys on an actual Windows Actions runner after compiling Inno
Setup. Linux source review or Rust cross-compilation cannot prove installer,
registry, Start Menu or uninstaller behavior. Record the native run and any
concrete repairs before calling packaging complete.

## Primary mechanism references

- [Inno non-administrative mode](https://jrsoftware.org/ishelp/topic_admininstallmode.htm)
  and [PrivilegesRequired](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
  define the per-user/no-elevation boundary.
- [Inno installation order](https://jrsoftware.org/ishelp/topic_installorder.htm)
  documents environment-change notification and uninstall ownership processing.
- [WM_SETTINGCHANGE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange)
  documents environment-consumer notification; it does not rewrite environments
  of already-running shells.
- [PowerShell environment variables](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_environment_variables)
  distinguishes Process, User and Machine scopes.

- [Rust static/dynamic C runtime linkage](https://doc.rust-lang.org/reference/linkage.html#static-and-dynamic-c-runtimes) documents the target-scoped `+crt-static` build.
- [GitHub Windows runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) lists the installed Inno compiler; the script still validates its actual path instead of assuming a downloaded bootstrap script is safe.

## Local evidence before native packaging

The source snapshot's collector ran successfully for 113 Cargo source packages
plus the toolchain library report and 12 referenced license files. Two independent
runs produced byte-identical notice text. Five stdlib Python tests cover missing
actual license failures, explicit and nested notices, workspace fixture exclusion,
verbatim Rust attribution copying and deterministic hash inventories. The native
PowerShell 7.6.9 parser reported zero syntax errors for both Windows scripts. None
of these checks substitute for compiling Inno Setup and running the generated
installer on Windows; that acceptance result belongs with the actual Actions run.

## Source inventory and publication integrity

Packaging requires a Git checkout and copies only tracked `docs` entries, not an
arbitrary recursive working directory. This deliberately excludes unrelated
untracked analysis, drafts and local fixtures. Installed users need neither Git
nor the build-time Python collector. Linux packaging honors `CARGO_TARGET_DIR`
and `SOURCE_DATE_EPOCH`; final archives remain under `dist`.

The pinned license supplement is marked byte-preserving in `.gitattributes`.
Windows checkout newline conversion must not change a resource whose actual
upstream bytes and SHA-256 were approved. Validation keeps the strict hash check;
it does not silently normalize a mismatching license into apparent success.

Publication resolves existing lightweight or annotated tags to the exact tested
commit, creates only a missing matching tag and rechecks before publication.
GitHub ignores `target_commitish` when a tag already exists, so merely setting
that parameter cannot establish a corresponding-source guarantee. Published
versions are never overwritten. A retry may resume a matching draft, but changed
or unknown draft assets fail rather than being silently replaced/published.
Local Node tests exercise tag mismatch/dereference/races, partial upload failure
and immutable public releases. The actual first main-branch publication must
still verify the real GitHub API and downloaded asset digests.

## Maintainer release procedure

1. Change the package version in `Cargo.toml` and update `Cargo.lock`; add matching
   `docs/releases/VERSION.md` notes. Use a numeric stable version for this workflow.
2. Open a PR. Package diagnostics exercise both platforms and the real Windows
   install/uninstall lifecycle; no branch protection or required-check gate is
   configured by these files.
3. Merge into `main`. A relevant main push builds/test packages and creates the
   exact source tag, uploads all assets into a draft, then publishes it. Do not
   pre-create a tag at an unrelated commit. A published version is never replaced;
   bump the version for a changed distribution.
4. `workflow_dispatch` defaults to build-only. To recover publication, run it on
   main with `publish=true`, or retry only the failed publish job to reuse the
   same artifacts. A changed/foreign draft or tag is an explicit operator decision,
   not something automation force-overwrites.
5. Verify the public asset names, downloaded SHA256SUMS and tag commit. Windows
   signing is a separate publisher-certificate integration; do not describe an
   unsigned package as signed/trusted just because its checksums match.
