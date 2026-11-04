# msync for Visual Studio Code

This extension provides native TextMate highlighting for `.msync` files and an
LSP client for the installed **msync 0.6+** executable. The grammar works before
the server starts and remains available if the executable is missing. Semantic
tokens, diagnostics, completion, hover, document symbols and formatting are
negotiated with the server; the extension does not reimplement the compiler.

## Install and try

Requires VS Code 1.91 or newer, Node/npm for development, and msync on PATH.
Release users do not need Node/npm: download `msync-language-0.7.3.vsix` and
`SHA256SUMS` from the same msync release, verify the VSIX's checksum, then use
**Extensions: Install from VSIX**, or:

```sh
code --install-extension msync-language-0.7.3.vsix
```

The extension does not bundle the msync executable; install the matching platform
package separately and configure its path as described below. For development,
run from this directory:

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

Set the user/machine setting below if msync is not on PATH. Changes restart the
server automatically; **msync: Restart Language Server** also reconnects without
reloading the window. Startup errors offer **Open Settings**. This is an executable
path, not a shell command; spaces do not need shell quoting:

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

- Unquoted endpoint URIs have three distinct display roles: in `ssh://forge`,
  `ssh` is the protocol, `://` is the operator, and `forge` is the address.
  The address remains one region even with a user, IPv6 host, port or path.
  Quoted URLs stay ordinary Strings. Custom semantic `protocol` and `endpoint`
  roles inherit standard type/string styling and have TextMate theme fallbacks.

- `$` is a unary lookup operator; `::` qualifies a member/path and `/` composes
  paths. Each has its own operator scope rather than coloring an entire
  `$local::directory/file` expression as one namespace. Quoted strings, URI
  endpoints and regex selector bodies keep their literal coloring. Word selection
  and completion replacement keep operators separate from names.
- With the matching 0.7.3 server, `$local::` and local endpoint aliases complete
  files and directories in the selected local context. Filename completions
  carry precise replacement ranges and keep ordinary filenames as Path operands.
  Names with spaces, syntax punctuation or a lexical-name collision are quoted
  when necessary to select the actual filename rather than a binding.

- All 0.6 control/declaration words, boolean and comparison operators, range
  pipelines, function names/parameters, namespaces, literal Strings, selectors
  and comments use standard theme-compatible TextMate scopes.
- The 0.6.5 vocabulary recognizes canonical builtin `$view::filter(...)` APIs,
  scope-qualified calls such as `$wsl::codex(config.toml)`, nested `::` expressions, ordered
  `[ ... ]` ranges and `if release` / `if acquire` / `if acqrel` headers. Native
  coloring is lexical; the Rust language server owns inference and validation.
  These typed expressions require the msync 0.6.5 server; earlier `views::` and
  `ranges::` spellings retain their coloring.
- With msync 0.7, `$std::fs`, `$std::path`, `$std::bool`, `$std::string` and
  `$std::ranges` offer member completion, hover, signatures and definition jumps.
  Aliases such as `fs => $std::fs` and explicit `scope name:` members retain their
  declaration identities. **Go to Definition** opens the executable's actual
  embedded standard-library source as a read-only document; navigation works
  inside that document too. There is no dependency on the repository checkout
  or a writable installation directory. `native` declarations expose the host
  boundary directly, while ordinary source functions show their implementation.
- A regex selector is a path component, not a less-than comparison. Its character
  classes and escapes shield `>` and `#`. Unfinished selectors recover at the
  next line. Quoted Strings preserve every backslash literally, including Windows
  `\target` components; a quote inside a String is doubled (`"a""b"`). Strings
  can span physical lines, which remain colored as String content until closing.
- `#` toggles line comments; quotes, parentheses and range brackets can auto-close
  outside strings/comments. Block headers increase indentation, with indentation-based
  folding. Snippets cover functions, macros, conditions, both loop forms, ordered
  ranges, scope/native declarations and canonical `$std::ranges::` pipelines. An unfinished range does not create
  a persistent string/regex scope that colors unrelated later statements.
- Use **Developer: Inspect Editor Tokens and Scopes** to distinguish native
  TextMate scopes from semantic tokens supplied by the msync server.

## Verification and dependencies

`npm test` loads the actual `vscode-textmate` engine and `vscode-oniguruma` WASM
rather than approximating Oniguruma with JavaScript regexes. It checks scopes,
literal backslashes, doubled quotes, multiline Strings, selectors/comparisons,
UTF-16 token coverage, incomplete editing and manifest/configuration consistency. Separate mocked-host tests exercise client
activation, executable paths containing spaces, document selection, failure
messages and idempotent shutdown. End-to-end server protocol behavior also has
Rust integration coverage; the mocked unit tests are not the native-host gate.

The separate native-host gate uses Microsoft's `@vscode/test-electron` to run
the **production bundled extension** inside a real Extension Development Host:

```sh
MSYNC_TEST_SERVER_PATH=/absolute/path/to/frozen/msync xvfb-run -a npm run test:integration
```

For a protocol-highlighting-only change, use `MSYNC_TEST_PROTOCOL_ONLY=1` with
that command. This selects a separate native-host suite that checks URI protocol,
separator and atomic address semantic ranges, quoted-URL String behavior and
production theme fallbacks without rerunning the unrelated editing journey.

Use Node 22+ for the test runner. On a desktop with an X11 display, omit
`xvfb-run -a`; the Linux runner explicitly selects X11 rather than inheriting
an unrelated Wayland session. The official runner manages its VS Code download
under `.vscode-test`; `MSYNC_TEST_VSCODE_VERSION` can pin the editor version, or
`MSYNC_TEST_VSCODE_EXECUTABLE` can reuse an existing managed executable.

Every run isolates HOME, temporary files, editor profiles, extensions and its
workspace below the repository `.temp` directory before loading the official
runner. It records the frozen server's SHA-256, host version, phases and output
logs. The suite checks real diagnostics, clearing after an edit, formatting,
range and filesystem completion (including applying the selected file edit),
operator semantic tokens and UTF-16 ranges. A transfer/delete fixture
must leave its output absent and its victim unchanged throughout. Failures
retain diagnostic, configuration and active-extension details alongside the
host logs. Xvfb runs exercise the real host but are not a human visual review.

The real-host journey passed on 2026-11-04 with VS Code **1.144.0** and the
production esbuild bundle against **msync 0.7.3**. It applied root-file,
spaced-filename, lexical-collision and hidden `.codex/` directory/file completions
through VS Code's actual completion edits, reported and cleared a bool diagnostic, formatted the document, and navigated
both lexical symbols and embedded standard-library sources. A real formatting
edit preserved ordinary Windows backslashes, a trailing backslash, doubled quotes
and multiline String contents; the Windows literal retained its exact String
semantic token and had no diagnostics. All **116 semantic tokens** had valid
UTF-16 ranges, including independent `$`, `::` and `/`
operators. **msync: Restart Language Server** reconnected and retained completion.
Transfer outputs remained absent and the delete victim unchanged throughout.
This was an automated Xvfb Extension Development Host session, not a mocked editor.

A separate protocol-only native-host run on the same date checked **10 URI
forms** and **60 semantic tokens**: protocol/operator/address separation, complete
and unfinished IPv6 addresses, Unicode address paths, an empty address while
editing, quoted-URL Strings and unchanged namespace tokens. The production
manifest supplied custom semantic-type inheritance and TextMate fallbacks.
This targeted run did not repeat the already-accepted full editing journey.

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
