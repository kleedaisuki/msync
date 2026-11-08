# msync for Visual Studio Code

This extension provides native TextMate highlighting for `.msync` files and an
LSP client for **msync 0.8**. The lexical grammar remains available while the
server starts or when the executable is missing. Type diagnostics, completion,
hover, navigation, document symbols, formatting and semantic tokens are
negotiated with the installed compiler, not reimplemented by the extension.

## Install and develop

Requires VS Code 1.91 or newer and the matching msync executable on PATH.
Install `msync-language-0.8.0.vsix` from the release with **Extensions: Install
from VSIX**, or:

```sh
code --install-extension msync-language-0.8.0.vsix
```

The VSIX does not bundle the executable. Set `msync.serverPath` to its absolute
path if it is not on PATH; it is an executable path, not a shell command.
Changes restart the server, as does **msync: Restart Language Server**. Startup
errors link to the setting; detailed diagnostics appear in **Output → msync
Language Server**.

```json
{
  "msync.serverPath": "/absolute/path/to/msync"
}
```

The extension launches the executable directly with `--language-server`, uses
stdio, and stops it on deactivation. File, untitled and embedded standard-library
documents are supported. Server-path configuration is machine-scoped. It does
not add filesystem watchers, shell invocation or a separate network transport.

From this directory, using Node 22 or newer:

```sh
npm ci --ignore-scripts
npm run build
npm test
code --extensionDevelopmentPath="$PWD" /path/to/script.msync
npx --yes @vscode/vsce@4.1.0 package --allow-missing-repository
```

The prepublish hook bundles Microsoft's language client and protocol libraries
with esbuild. Runtime dependencies and notices are packaged; a `node_modules`
tree is not needed in the installed VSIX.

## Language editing

```text
fn copy(file: Path | String, destination) {
    file -> destination
};
let file? = if exists ~/.codex { ~/.codex } else { null };
match file {
    null => {},
    _ => { copy(file, $wsl::file) },
};
```

- Blocks use braces; indentation is an editing preference, not syntax. Brace,
  parenthesis and range brackets auto-close outside Strings and comments. `//`
  toggles line comments; existing `#` comments remain colored.
- `fn` declarations and anonymous functions have parameter/function roles.
  `let` bindings, nullable `?`, `null`, explicit types, `match`, `decltype`,
  `is` tests and `native fn` signatures have distinct theme-compatible scopes.
  Removed `async`, `await` and `delete` words are no longer control keywords.
- Snippets cover named/anonymous functions, nullable bindings, conditions,
  exhaustive matches, range loops, lexical scopes, native signatures and SSH
  connections. A block's final expression retains its value; `;` discards it.
- `$`, `::` and `/` have independent operator scopes. Namespaces, function
  callbacks, range pipelines and ordered `[ ... ]` ranges keep their lexical
  roles. Filename components such as `root/match/file` are not keywords.
- An unquoted URI keeps three roles: protocol, `://` operator and atomic address.
  IPv6 brackets, ports and Unicode paths stay part of the address. A closing
  semicolon or brace is not swallowed into the URI. Quoted URLs are Strings.
- Regex selectors and match patterns retain character-class/escape coloring.
  Classes shield `>` and `#`; unfinished selectors recover on the next line.
  Strings preserve literal backslashes and doubled quotes, including multiline
  contents. No C-style String escape semantics are added by the highlighter.
- Embedded standard-library navigation uses the `msync-stdlib` document provider
  and requests source from the running executable, independent of checkout or
  installation layout. The compiler remains the authority for semantics.

## Verification

`npm test` uses the actual `vscode-textmate` engine and Oniguruma WASM. It checks
brace/semicolon syntax, typed and anonymous functions, nullability/type patterns,
URI boundaries, regex shielding, Unicode and multiline literal Strings.
Mocked-host tests separately exercise client startup, paths with spaces,
settings repair, stdlib document fetching, restart serialization and shutdown.

The native Extension Development Host journey uses the production bundle:

```sh
MSYNC_TEST_SERVER_PATH=/absolute/path/to/msync xvfb-run -a npm run test:integration
```

Omit `xvfb-run -a` on an existing X11 desktop. The runner isolates HOME,
configuration, temporary files, editor profiles and the test workspace under the
repository `.temp`. It records executable and adjacent managed-assembly SHA-256 values, VS Code
version, logs and phases.
`MSYNC_TEST_VSCODE_EXECUTABLE` reuses an installed test editor;
`MSYNC_TEST_VSCODE_VERSION` selects a download version. The journey exercises a
nullable diagnostic and a correcting edit, brace formatting, generic-function
navigation, completion, hover, symbols, UTF-16 semantic ranges and server restart.
A transfer fixture must remain unexecuted throughout. `MSYNC_TEST_PROTOCOL_ONLY=1`
selects the narrower semantic URI journey, including incomplete edits.

The 0.8 native-host journey passed on 2026-11-08 using VS Code **1.144.0**, the
production esbuild extension bundle and the C# development apphost. It reported
the nullable diagnostic, cleared it after an incremental correction, applied
**11 formatting edits**, navigated the generic function, exercised completion,
hover and symbols, validated **34 UTF-16 semantic tokens**, and restarted the
server. The transfer output remained absent throughout. The focused protocol
journey also passed for **10 URI forms** and **74 semantic tokens**, including
incomplete/complete IPv6, Unicode addresses, empty addresses and quoted URLs.
These automated Xvfb runs exercise the actual Extension Development Host; they
are not a human visual review. The historical 0.7 evidence is not used as
acceptance of the rewritten compiler.

References:
[VS Code syntax highlighting](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide),
[language configuration](https://code.visualstudio.com/api/language-extensions/language-configuration-guide),
[LSP extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide),
[official extension testing](https://code.visualstudio.com/api/working-with-extensions/testing-extension),
[Microsoft TextMate engine](https://github.com/microsoft/vscode-textmate).
