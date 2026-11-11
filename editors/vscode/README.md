# msync for Visual Studio Code

This extension provides native TextMate highlighting for `.msync` files and an
LSP client for **msync 0.8**. The lexical grammar remains available while the
server starts or when the executable is missing. Type diagnostics, completion,
inferred signatures on hover, navigation, document symbols, formatting and
semantic tokens are negotiated with the installed compiler, not reimplemented
by the extension.

## Install and develop

Requires VS Code 1.91 or newer and the matching msync executable on PATH.
Install `msync-language-0.8.6.vsix` with **Extensions: Install
from VSIX**, or:

```sh
code --install-extension msync-language-0.8.6.vsix
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
    _ => { copy(file, $wsl @ file) },
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
- `$`, `::`, `@` and `/` have independent operator scopes. Named standard
  modules, callbacks, pipelines and ordered `[ ... ]` ranges keep their lexical
  roles. `connection @ path` selects a physical endpoint; `scope::member`
  selects a lexical member. Filename components such as `root/match/file`
  are not keywords.
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

### Inferred colors and inspection

Function values use the theme's **function** color, including scope members and
aliases such as `let callback = Module::operation;`. The `fn` and `scope`
keywords use the theme's **keyword** color in every declaration form. Names of
named `scope Name { ... }` declarations and their aliases use the **type** color,
matching explicit type names and concept constraints. Standard modules such as
`$std::math` are named scopes and use the same type role. Values of anonymous
`scope { ... }` expressions
use the ordinary **identifier** color, including
instances returned by constructor-like functions. Their inferred type is still
Scope: hover and completion retain their declared members. Colors
come from your VS Code theme; the extension does not impose a dark-only palette.
Semantic highlighting is enabled by default for msync and can still be disabled
in your language-specific editor settings. Before inference finishes, direct
`fn` and `scope` declarations retain matching TextMate fallback roles.
`if`, `else`, `match`, `as`, `acquire`, `release`, `acqrel` and the null-test `?`
use the theme's separate
**control-flow keyword** role; no RGB colors are hardcoded. Every Scope member
can be inspected, including scalar and Connection-valued members.

Hover a name to inspect its actual inferred type, function signature or Scope
members. Generic functions acquire parameter and result types from their calls;
inspection never executes the program. A Scope is a lexical collection, not a
physical Connection:

```text
scope MyClass {
    new: fn() { scope { x: ~/.codex } },
    get: fn(instance) { instance::x },
};
let instance = MyClass::new();
MyClass::get(instance) -> ./;
```

Keep the constructor's Scope when passing it to `get`. Adding `::x` to the
binding selects the Path instead, so `instance::x` inside `get` can no longer
select a Scope member. Diagnostics identify the failing expression and retain
the call context rather than repeating the document URI in the message.

**Format Document** uses the compiler's formatter with an 80-column target.
Arguments and expressions wrap at syntax boundaries. Long boolean expressions
use a grouped continuation with leading `and`/`or`, so the operator remains
visible beside each operand. Nested blocks keep their
four-space indentation; less horizontal room causes earlier wrapping, not a
flattened block structure. Indivisible paths, identifiers, URLs, literal contents
and comments may exceed the target rather than changing their meaning. The
extension selects this formatter for msync by default without changing other
languages' formatting preferences.

### Numbers, conversions and structural concepts

Number literals include decimals, exponents, base prefixes and separators:

```text
let counts = [1_000, 1.5, .5, 1e3, 0xFF, 0b10_01, 0o77, -0];
let file = "./input" as Path;
let selected = $local @ file;
concept Named { name: String, };
concept Labeled { Named, };
fn label(item: Labeled) -> String { item::name };
let instance = scope { name: "Klee", };
let title = label(instance);
```

Concepts describe required members and callable signatures without opening the
runtime type universe. Named constraints participate in completion, hover,
definition navigation and the document outline. A concept name is a type;
`concept` and inline `typename` remain declaration keywords. Associated names
are introduced at their first requirement and reused as type constraints:

```text
concept Product {
    first?: typename First,
    second?: typename Second,
};
fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? {
    value
};
let selected? = preserve($std::data::Option::some("Klee"));
```

The standard concepts participate in type-context completion, including each
step of `$std::concepts::Option`. Completing a segment replaces only that
segment; it does not duplicate `$` or its namespace qualifier. Hover shows the
actual requirement and its source documentation. Definition navigation opens
the executable's embedded `concepts.msync`; associated names navigate to their
inline binder. Concepts are compile-time constraints, not runtime Scope fields.

Named class Scopes group constructors and associated operations. Instances
remain ordinary data Scopes, with explicit receiver arguments and policy
Scopes checked by concepts:

```text
scope SameNumber {
    Self: Number,
    equal: fn(left: Number, right: Number) -> Bool { left == right },
};
let values = $std::containers::EqualitySet::with_equality([3, 1], SameNumber);
$std::containers::EqualitySet::insert(values, 2);
let report = $std::files::File::new(./report.txt);
```

Class names use the type role; their snake_case function names use the
function role. Completion replaces the exact function identifier, such as
`with_equality` or `read_text`, without changing its qualifier. Policy calls specialize through the
normal generic `fn`/`scope` mechanism, not through a runtime method registry.
Named families live under `containers`, `data`, `files`, `commands`, `text` and
`regex`; `io` and `process` retain their lower-level primitives.

Conversion uses `as`,
not a dynamic-looking cast helper. Only Path/String conversion is implicit.
Numeric-looking filename components retain path roles rather than being
partially colored as numbers; inferred semantic tokens refine the fallback.

## SSH authentication during completion

A selected remote path completion can ask the native SSH client to inspect that
endpoint. The editor displays its original prompt with the endpoint identity:
passwords and passphrases use a hidden input box; host-key confirmation offers
explicit Yes/No choices; security-key touch notices do not block the client.
Cancelling the request or restarting the server closes its owned prompt. Prompts
from different endpoints are queued rather than replacing each other. Responses
are returned directly to native SSH, not written to the output channel or stored
in extension settings.

## Inspect semantic HIR and compiled bytecode

Open a `.mhir`, `.mso` or `.mexe` file with **msync Typed Artifact**, the default
read-only custom editor. The outline and reference links navigate within the
artifact, even after its original source has been removed. The view uses your
editor theme and has Find support.

Typed HIR is the compiler's semantic representation, not bytecode decompiled
into apparent source. Its view distinguishes types, declaration and location
identities, structured expressions, function identities and capture operations.
Its own format version is independent from bytecode. The bytecode views show
their stage, actual format and language versions, optimization level, constants,
functions, captures, basic blocks, instructions and bound context. Format-5
executable artifacts also show contextual execution plans: concrete transfer
kinds, resource footprints, dependency edges, batches and eliminated writes.
Plan and node references participate in the outline and navigation. Inspecting a
plan never refreshes its observations or checks whether it is still executable.

Both binary stages use independent IR highlighting: function, block, constant
and local references have distinct roles; branches and acquire/release markers
are control flow rather than ordinary opcodes. Field names and checked layout
slots remain recognizable beside types, escaped path/String data and exact
numeric representations, including hexadecimal bits, negative zero and NaN.
The custom view reads VS Code theme colors; the text view uses standard TextMate
scopes. HIR type and location references have their own roles, and structured
branches and loops remain control flow. No artifact view sends generated IR
through the source-language checker.

**Open IR as Text** opens the same decoded data in a normal read-only editor,
with `msync-ir` highlighting, Outline, hover explanations and Go to Definition.
The command is also available as **msync: Open Artifact as Read-only IR** in the
Command Palette and the Explorer context menu. **msync: Refresh Artifact View**
reloads after external compilation; file changes and language-server restarts
also refresh open views. A corrupt or unsupported artifact displays the reader's
diagnostic with retry and reader-settings actions rather than a binary editor.

File-backed artifacts share one synchronous directory notification subscription
per open folder, filtered to the displayed filenames. This catches fast atomic
compiler publication even while VS Code's initial workspace watcher is still
starting. Other filesystem schemes use the workspace provider. If native file
notifications fail, the extension explains the failure and falls back to that
provider; manual Refresh remains available. No polling or recursive watcher is
added by the extension.

The reader is the configured `msync.serverPath` language server. The extension
reads bytes through VS Code's workspace filesystem, including remote workspaces,
and sends them to the native semantic-HIR or bytecode reader; it does not contain
a second JavaScript binary parser. Inspection never runs the artifact, binds
paths to a new context, authenticates endpoints, or changes the original file.
The view renders actual semantic data or optimized bytecode; it does not claim
to recover discarded source.
Available source references open only when explicitly selected.

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
repository `.temp`. It records executable and adjacent managed-assembly SHA-256
values, VS Code version, logs and phases.
`MSYNC_TEST_VSCODE_EXECUTABLE` reuses an installed test editor;
`MSYNC_TEST_VSCODE_VERSION` selects a download version. The journey exercises a
nullable diagnostic and a correcting edit, brace formatting, generic-function
navigation, completion, hover, symbols, UTF-16 semantic ranges and server restart.
The screenshot regression additionally checks constructor-returned Scope values,
function/Scope aliases, inferred semantic roles, useful hovers, 80-column
formatting and member-access diagnostics with call context. A transfer fixture
must remain unexecuted throughout. `MSYNC_TEST_PROTOCOL_ONLY=1`
selects the narrower semantic URI journey, including incomplete edits.

`MSYNC_TEST_STANDARD_CONCEPTS_ONLY=1` selects the independent standard-concept
journey: inline associated binders, embedded definitions, qualified constraints,
snake_case policy completion and named container families under light and dark
themes. It then checks and runs the editor-produced source with that same
fingerprinted executable, verifying its Option and ordered-container results
inside the isolated workspace. It does not rerun the unrelated artifact suite.

`MSYNC_TEST_HIR_ONLY=1` selects the semantic artifact journey. It exports `.mhir`
with the same fingerprinted executable, removes the source, and inspects the
actual native semantic model in light and dark themes. The journey checks
Outline, hover, internal identity navigation, readonly bytes, external atomic
publication and corruption/repair refresh. Compilation and viewing must leave
the program's transfer output absent. This selector requires a server with the
Typed HIR export interface; mocked presentation tests do not establish native
reader or extension-host acceptance.

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

The refined product-polish host journey in `.temp/vscode-host-IP92gO` validated
18 semantic roles: named Scope declarations and aliases, anonymous Scope
instances, declaration keywords, function values, explicit types, control
flow and `?`. It inspected Connection, Bool, String and Path members, displayed
`fn get(some: Scope) -> Path`, wrapped the long call to 42 columns, and retained
the typed call context on the failing member expression. It used VS Code 1.144.0
and fingerprinted the development compiler assemblies in its isolated metadata.

References:
[VS Code syntax highlighting](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide),
[semantic highlighting and theme fallback](https://code.visualstudio.com/api/language-extensions/semantic-highlight-guide),
[language configuration](https://code.visualstudio.com/api/language-extensions/language-configuration-guide),
[LSP extension guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide),
[official extension testing](https://code.visualstudio.com/api/working-with-extensions/testing-extension),
[Microsoft TextMate engine](https://github.com/microsoft/vscode-textmate).
