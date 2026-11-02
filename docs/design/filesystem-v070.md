# Filesystem standard library in v0.7.0

## Design and source ownership

`stdlib/fs.msync` is the navigable public API under `$std::fs`. Its native
signatures are checked centrally by `src/stdlib.rs`; the runtime delegates to
`src/execution/native_fs.rs` and the purely lexical
`src/execution/native_fs_path.rs`. An inferred alias such as `fs => $std::fs`
retains the same namespace identity. Source-defined `current_path`, `relative`,
`proximate`, `status_known` and `copy` compose ordinary typed language operations;
all actual filesystem effects belong to explicit native calls.

The surface follows the [C++ filesystem operation specification](https://eel.is/c++draft/fs.op.funcs)
and [path specification](https://eel.is/c++draft/fs.class.path). msync uses native
Rust filesystem operations locally and the established native SSH/WSL transport
with GNU tools remotely; it does not introduce Python, a resident daemon, an
independent credential stack or a persistent recovery protocol.

| Family | API | Result |
| --- | --- | --- |
| Observation | `exists`, `is_regular_file`, `is_directory`, `is_symlink`, `is_block_file`, `is_character_file`, `is_fifo`, `is_socket`, `is_other`, `is_empty`, `equivalent`, `is_readonly` | `Bool` |
| File type | `status`, `symlink_status` | `String` tag |
| Directory entries | `directory_iterator`, `recursive_directory_iterator` | `Range<Path>` |
| Resolution | `absolute`, `canonical`, `weakly_canonical`, `read_symlink`, `temp_directory_path`, `current_path`, `relative`, `proximate` | `Path` |
| Creation/removal | `create_directory`, `create_directories`, `remove`, `remove_all`, `copy_file` | `Bool` |
| Mutation | `delete`, `rename`, `create_hard_link`, `create_symlink`, `create_directory_symlink`, `copy_symlink`, `set_permissions`, `set_readonly` | `Void` |
| Transfer | source-defined `copy` | ordinary transfer statement |
| Permissions | `permissions` | `Range<String>` flag names |
| Decomposition | `root_name`, `root_directory`, `root_path`, `relative_path`, `parent_path`, `filename`, `stem`, `extension`, `remove_filename` | `Path` |
| Lexical composition | `lexically_normal`, `lexically_relative`, `lexically_proximate`, `replace_filename`, `replace_extension`, `append`, `concat`, `make_preferred` | `Path` |
| Path components | `components` | `Range<Path>` |
| Path representation | `string`, `generic_string` | `String` |
| Path predicates | `path_empty`, `has_root_name`, `has_root_directory`, `has_root_path`, `has_relative_path`, `has_parent_path`, `has_filename`, `has_stem`, `has_extension`, `is_absolute`, `is_relative` | `Bool` |
| Pure status predicate | `status_known` | `Bool` |

`status` follows symbolic links; `symlink_status` observes the directory entry.
The tags are `regular`, `directory`, `symlink`, `block`, `character`, `fifo`,
`socket`, `unknown`, and `not_found`. A missing path or an unresolvable child of
a nondirectory becomes `not_found`/false. Native I/O diagnostics preserve permission failures, symlink
cycles, the endpoint and the operated path.

Permissions are a finite symbolic domain, not a numeric quantity disguised as
text. `permissions(path)` returns individual C++ names: `owner_read`,
`owner_write`, `owner_exec`, `group_read`, `group_write`, `group_exec`,
`others_read`, `others_write`, `others_exec`, `set_uid`, `set_gid`, `sticky_bit`.
`set_permissions(path, "owner_read|owner_write|group_read")` replaces the mode;
`owner_all`, `group_all`, `others_all`, `all`, `none` and conventional octal mode
strings such as `"0644"` are also supported. Unix/SSH/WSL use native POSIX bits.
Windows exposes its native readonly/writable mapping through Rust permissions;
mode writes map the presence of any write flag to that native attribute, not to
fabricated POSIX ACLs. `is_readonly` and `set_readonly(path, Bool)` expose the
portable attribute directly. The readonly query describes metadata, not effective
access after ACL, identity or privilege evaluation.

## Compatibility and intentional differences

- Existing prefix `exists` and statement `delete` keep their previous behavior.
  `$std::fs::delete` is the same strict file/symlink unlink: directories and
  missing paths are errors. New `remove` removes a file, symlink or empty
  directory and returns false if missing. `remove_all` recursively removes
  without following child symlinks and returns whether the root existed.
- `remove_all` returns `Bool`, not the C++ removal count: the language has no
  numeric domain. File size, hard-link count, resize, last-write timestamps and
  space capacity/free/available require numeric/time/record types; v0.7.0 does
  not stringify those quantities or invent an arithmetic subsystem.
- `copy` is source-backed ordinary msync transfer syntax. `copy_file` reuses
  the staged transfer implementation and normal overwrite behavior. It does
  not introduce C++ copy_options, rollback journals or extra authenticated
  metadata preflight. Normal transfer and retry remain the publication model.
- `current_path` returns the caller's captured working directory. There is no
  process-global cwd mutation: lexical `$pwd` contexts, not process-wide `chdir`,
  determine relative names in this language.
- Iteration yields complete endpoint-bearing paths, including hidden files,
  with deterministic names and without traversing directory symlinks. Native
  names outside the language's UTF-8 string domain produce an owned diagnostic;
  entries are never silently omitted or lossily renamed.
- Lexical operations inspect neither filesystem nor transport. Strings remain
  lexical data, so `has_root_path("relative")` remains false and
  `replace_extension(path, ".txt")` does not acquire a cwd prefix. The result
  retains the source endpoint. SSH/WSL path grammar is POSIX even on Windows.
  See [the lexical path evidence](filesystem-path-v070.md).

## Runtime mechanisms and verification

Local effects use [Rust `std::fs`](https://doc.rust-lang.org/std/fs/), including
its platform implementations of recursive deletion and link handling. A native
call runs in the existing blocking-worker integration; cancellation is checked
before admission and between recursive entry visits. One filesystem call is
not a transaction, and completed directory members remain visible on failure.

Remote calls send operands as positional arguments through the existing
transport quoting owner. File names containing whitespace, Unicode, newlines or
shell metacharacters are data. Path and directory responses are NUL-framed,
not newline-delimited. `stat` is run under the C locale and only ENOENT/ENOTDIR
become absence; other native diagnostics are retained. Recursive enumeration uses
`find -H` so the root directory may itself be a link, while child links are not
traversed. No arbitrary directory-entry or filename quota is introduced.

The focused backend journeys exercise actual local directory creation,
observation, atomic file copy, hard links, rename and removal; dangling and
out-of-tree symlinks; weak canonical resolution with a missing/parent tail; and
the actual remote helper shell against paths containing quotes, `$()` syntax,
newlines and Unicode. They check NUL-framed results, recursive link behavior,
copying a link without changing its target text, and preservation of symlink
cycle errors. Integration acceptance additionally runs source-free compiled
objects and the real native SSH path; results are recorded with the release
validation rather than inferred from helper-only tests.

Local focused results for this implementation:

- `cargo test --locked --lib native_fs`: 9 passed (five native filesystem
  journeys and four lexical/path tests, including normalization properties).
- `cargo test --locked --test filesystem_v070`: 2 passed. With the source removed,
  both executable objects and JSON IR retained permission flags/readonly,
  lexical strings, path components, recursive native ranges, copied bytes and
  removal results. A separate Unix object journey retained relative symlink
  content exactly as `target` rather than capturing the process cwd.
- The extracted native remote script also passed direct permission replacement,
  readonly toggling, literal relative link creation and newline-preserving link
  copying. The directory decoder retained all 1,500 entries in a response
  substantially larger than the 64KiB transport buffer. That argument configures
  buffering; the existing transport drain has no output-size quota.
- `cargo clippy --locked --lib --test filesystem_v070 -- -D warnings` passed;
  owned Rust files passed rustfmt and the integrated diff passed whitespace checks.

## Nondirectory ancestors are absence, not an observation failure

A focused libstdc++ oracle for a regular `file` followed by `/child` returned
`file_type::not_found` (the nonthrowing overload retained ENOTDIR), while the
throwing `exists` and `is_directory` APIs both returned false. Rust's
`std::fs::exists` instead returned `ErrorKind::NotADirectory`. Following
[C++ status resolution semantics](https://eel.is/c++draft/fs.op.status), native
status/predicates and the existing `exists` helper classify both NotFound and
NotADirectory as absence only in their error branch. The remote GNU stat helper
uses the equivalent C-locale ENOENT/ENOTDIR suffixes. Permission errors and
symlink cycles still propagate; no new success-path metadata query was added.
The oracle and fixture live under `.temp/native-fs-v070/enotdir`.
The final ENOTDIR slice passed the native local lifecycle check, the legacy
existence lifecycle check, and both actual remote-shell checks (four test
functions total). `cargo clippy --locked --lib -- -D warnings` passed afterward;
formatting and whitespace checks remained clean. Already accepted unrelated
filesystem journeys were not repeated.
