# msync for Visual Studio Code

This extension provides native TextMate highlighting for `.msync` files and an
LSP client for the installed **msync 0.6+** executable. The grammar works before
the server starts and remains available if the executable is missing. Semantic
tokens, diagnostics, completion, hover, document symbols and formatting are
negotiated with the server; the extension does not reimplement the compiler.

## Install and try

Requires VS Code 1.91 or newer, Node/npm for development, and msync on PATH.
From this directory:

```sh
npm ci --ignore-scripts
npm run build
npm test
code --extensionDevelopmentPath="$PWD" /path/to/script.msync
```

Alternatively build, open this directory in VS Code and press F5 using the included
`Launch msync extension` configuration. To build a locally installable VSIX, use
the official packager, then **Extensions: Install from VSIX**:

```sh
npx --yes @vscode/vsce@4.1.0 package --allow-missing-repository
```

The prepublish hook bundles the runtime client and protocol libraries with
`esbuild`, keeping the native grammar and third-party license notices beside
the bundle. The VSIX does not need a `node_modules` tree at runtime.

Set the user/machine setting below if msync is not on PATH, then reload the
window. This is an executable path, not a shell command; spaces do not need
shell quoting:

```json
{
  "msync.serverPath": "/absolute/path/to/msync"
}
```

The client starts the executable directly with `--language-server`, communicates
through stdio, and stops it during extension deactivation. File and untitled
msync documents are supported. Startup errors identify the cause and the setting
to repair; detailed client output appears in **Output → msync Language Server**.
There are no filesystem watchers, shell invocations or network transports added
by the extension. Server-path configuration is machine-scoped, not accepted from
an untrusted workspace setting.

## Language editing

- All 0.6 control/declaration words, boolean and comparison operators, range
  pipelines, function names/parameters, namespaces, string escapes, selectors
  and comments use standard theme-compatible TextMate scopes.
- A regex selector is a path component, not a less-than comparison. Its character
  classes and escapes shield `>` and `#`. Unfinished single-line input does not
  color the rest of the file as a string or regex.
- `#` toggles line comments; quotes and parentheses can auto-close outside
  strings/comments. Block headers increase indentation, with indentation-based
  folding. Snippets cover functions, macros, conditions and both loop forms.
- Use **Developer: Inspect Editor Tokens and Scopes** to distinguish native
  TextMate scopes from semantic tokens supplied by the msync server.

## Verification and dependencies

`npm test` loads the actual `vscode-textmate` engine and `vscode-oniguruma` WASM
rather than approximating Oniguruma with JavaScript regexes. It checks scopes,
escapes, selectors/comparisons, UTF-16 token coverage, incomplete editing and
manifest/configuration consistency. Separate mocked-host tests exercise client
activation, executable paths containing spaces, document selection, failure
messages and idempotent shutdown. End-to-end server protocol behavior also has
Rust integration coverage; the mocked unit tests are not the native-host gate.

The separate native-host gate uses Microsoft's `@vscode/test-electron` to run
the **production bundled extension** inside a real Extension Development Host:

```sh
MSYNC_TEST_SERVER_PATH=/absolute/path/to/frozen/msync xvfb-run -a npm run test:integration
```

Use Node 22+ for the test runner. On a desktop with an X11 display, omit
`xvfb-run -a`; the Linux runner explicitly selects X11 rather than inheriting
an unrelated Wayland session. The official runner manages its VS Code download
under `.vscode-test`; `MSYNC_TEST_VSCODE_VERSION` can pin the editor version, or
`MSYNC_TEST_VSCODE_EXECUTABLE` can reuse an existing managed executable.

Every run isolates HOME, temporary files, editor profiles, extensions and its
workspace below the repository `.temp` directory before loading the official
runner. It records the frozen server's SHA-256, host version, phases and output
logs. The suite checks real diagnostics, clearing after an edit, formatting,
range completion, semantic tokens and UTF-16 ranges. A transfer/delete fixture
must leave its output absent and its victim unchanged throughout. Failures
retain diagnostic, configuration and active-extension details alongside the
host logs. Xvfb runs exercise the real host but are not a human visual review.

The real-host gate passed on 2026-10-31 with VS Code **1.144.0**, official
`@vscode/test-electron` **3.1.0**, and the production esbuild bundle. The real
msync process reported the bool type error, cleared it after an edit, supplied
14 formatting edits, completed `views::filter`, and supplied 29 semantic tokens
including the exact Unicode string `"可莉🌟"`, function and parameter roles.
Both absolute transfer outputs remained absent and the delete victim's bytes
were unchanged. This was an automated Xvfb Extension Development Host session,
not a mocked editor or a human assessment of colors and layout.

Runtime dependencies are locked. As of 2026-10-31, the official latest stable
`vscode-languageclient` is 11.0.0 and still depends on `minimatch ^10.2.6`.
`npm audit` reports the upstream moderate
[globparts amplification advisory](https://github.com/advisories/GHSA-g3hv-75wg-m249),
whose published fix is minimatch 11.0.1. We do not force an incompatible major
replacement into Microsoft's client. This extension uses fixed document
selectors without glob patterns and launches a trusted, machine-configured
server; it does not accept user-supplied glob filters. Recheck the audit when
upgrading the client and remove this note when upstream supports the fix.

References:
[VS Code syntax highlighting](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide),
[language configuration](https://code.visualstudio.com/api/language-extensions/language-configuration-guide),
[LSP extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide),
[official extension testing](https://code.visualstudio.com/api/working-with-extensions/testing-extension),
[Microsoft TextMate engine](https://github.com/microsoft/vscode-textmate).
