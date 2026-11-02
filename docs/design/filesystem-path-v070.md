# Endpoint-aware lexical paths in v0.7.0

`src/execution/native_fs_path.rs` owns purely lexical operations for `$std::fs`.
It does not stat, authenticate, resolve symlinks, or require a path to exist.
The native filesystem dispatcher calls `call(name, arguments, span)` and maps its
`PathValue::{Path, Paths, Bool, String}` to the interpreter's tagged values. An unknown
name returns `None`; an admitted operation with wrong arity is a source error.

## Surface and representation

- Decomposition: `root_name`, `root_directory`, `root_path`, `relative_path`,
  `parent_path`, `filename`, `stem`, `extension`.
- Queries: `path_empty`, `is_absolute`, `is_relative`, and the corresponding
  `has_*` decomposition predicates.
- Transformations: `lexically_normal`, `lexically_relative`,
  `lexically_proximate`, `remove_filename`, `replace_filename`,
  `replace_extension`, `append`, `concat`, `make_preferred`.
- Text conversion: `string`, `generic_string`.
- Ordered path-component iteration: `components`, returning `Range<Path>` including
  root components and a trailing empty component for a directory separator.

A Path result retains its input's physical endpoint. A String result contains
only its pathname text. In particular, `generic_string` does not serialize an
SSH URI. Two paths on different endpoints have no lexical relative path:
`lexically_relative` returns the empty path and `lexically_proximate` returns its
original path, extending the C++ incompatible-root rule to endpoint identity.

The grammar follows the target platform: SSH and WSL are POSIX on every host,
including a Windows interpreter. Native local/ENV/PATH use Windows drive and UNC
roots only on Windows. Backslashes in a remote path remain filename characters.
No character quotas or existence validation are added to a lexical operation.

## C++ semantics and implementation choices

The [C++ draft's pathname grammar and normalization](https://eel.is/c++draft/fs.path.generic)
and [path members](https://eel.is/c++draft/fs.path.member) are the semantic basis.
Normalization uses an ordinary component stack; decomposition retains original
spelling where relevant, rather than normalizing before selecting a member.
`filename("a/b/")` is empty, `remove_filename("a/b")` is `a/`, and
`parent_path("a/b")` is `a`. A leading filename dot is not an extension:
`.profile` has stem `.profile` and empty extension; `file.` has extension `.`.
This is not Rust `Path::file_name`, whose trailing-separator behavior differs.

## Focused evidence

A disposable harness under `.temp/fs-path` compiled the actual production module
against small endpoint/error stubs, alongside a C++17 `<filesystem>` oracle
compiled using the installed `g++`. Python `random.seed(700)` generated 1,000
POSIX strings by concatenating 0–11 samples of `a`, `b`, `.`, `..`, `/`, `//`.
The oracle and implementation agreed on all generated results for
`lexically_normal`, `parent_path`, `filename`, and `remove_filename`. The probe
caught and corrected trailing dot-dot normalization and repeated-leading-slash
parent decomposition before integration. It is not a Windows execution claim.

The module also contains focused C++ examples, endpoint-retaining typed dispatch,
host-independent Windows drive/UNC versus POSIX cases, and a property asserting
normalization idempotence for generated pathname strings. The Cargo integration
owner records their execution after native dispatcher registration.

The actual module's four tests and strict Clippy (including test code) also passed
in the isolated Cargo probe (`cargo test` / `cargo clippy --tests -- -D warnings`
against `.temp/fs-path/Cargo.toml`). That probe isolates lexical behavior while
other v0.7.0 compiler changes are being integrated; it does not replace the
parent's source-to-native-runtime acceptance journeys.
