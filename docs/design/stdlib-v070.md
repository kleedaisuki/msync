# v0.7 standard-library and native contract

The source root `$std` is a typed Scope, not a spelling prefix. Its `fs`,
`path`, `bool`, and `string` members load embedded source modules lazily;
`ranges` loads ordinary source wrappers while retaining typed lazy-range adaptor
identity for pipe-position primitives. Aliases use
normal inferred Scope bindings (`fs => $std::fs`). Existing `$view`, `views`
and `ranges` forms remain compatible. Parsed library documents are cached once
per process; source declarations are instantiated with the current compilation
context, never cached with a first caller's working directory or slot owner.

Sources live in `stdlib/*.msync`, are embedded through `stdlib::SOURCES`, and
are packaged under `share/msync/std`. The language server can expose the same
bytes through `msync-stdlib:///0.7.0/MODULE.msync`, so navigation is independent of
installation layout. Pure boolean/path composition and filesystem wrappers use
ordinary source functions/macros, preserving normal specialization. Lazy callback adaptors use actual inferred `native` source declarations that
forward to the compiler's dependent range capability resolver; first-class
callback/function and numeric count types are not fabricated as runtime values.

## Native declarations

```
scope fs:
    native is_directory(path) = "fs.is_directory"
    native directory_iterator(path) -> Range<Path> = "fs.directory_iterator"
    native delete(path) -> Void = "fs.delete"
```

The optional `-> Type` checks an explicit result assertion; omitting it infers
the result from the registered capability. Compiler-native range primitives use
dependent signatures without inventing runtime function types:

```
scope ranges:
    native of(values...) = "ranges.of"
    native filter(values, predicate) = "ranges.filter"
```

`values...` is an explicitly variadic native parameter, registered only for
capabilities with variadic admission. A compiler native specializes ordinary
source arguments and callbacks into the existing typed Range IR; it does not
forward AST/source to the runtime. Direct source-expressible wrappers such as
`take`, `reverse`, and `single` still execute ordinary source specialization.

A declaration binds a source name to a registered host capability. The compiler
checks implementation identity, arity and result type against `stdlib::signature`.
It lowers calls to typed Native expressions, Native range sources, or Native
control statements. The host implementation never receives source text or AST.
Void calls are statements; nonvoid results may also be discarded explicitly by
calling as a statement. Native result families include every existing scalar
type, Range of any supported item type, and Void.

The registry owns parameter admission, result types and effect classification.
Filesystem arguments accept Path or filename String, but not Bool. Pure lexical
path operations retain raw relative string spellings. Filesystem observations
and mutations use captured path context and actual selected endpoints. Runtime
waits only on overlapping admitted path arguments, not on unrelated jobs or
possible endpoints. Native Range sources evaluate on first demand; `take(0)`
does not invoke the native. New native opcodes negotiate `native_std_v1`;
old typed-control objects remain loadable.

## Namespace and range relationship

`scope NAME:` creates lexical members and captures their definition context.
Members and functions are selected with `::`; nested scopes and aliases use the
same semantic Scope type. Scalar members serialize as a Lexical namespace in
source-free objects. Function/macro templates remain compile-time definitions,
as established for non-namespaced lexical functions. Range<Scope> supports real
endpoint values and lexical value namespaces, without widening ordinary scalar
unions or adding a heap vector to every scalar type.

A range's unqualified path must retain its unbound endpoint/home provenance:
`[~/.codex/file]` followed by `remote::file` resolves home on the selected endpoint,
not the compiler's local machine. Explicit `$pwd`/`$local` projections remain
context-bound and are not silently reinterpreted as remote paths.

### Dynamic qualifier argument resolution (2026-11-03)

The compact `scope::file` syntax reaches `resolve_path`, whereas a general
selection expression reaches `resolve_select`. The former's dynamic-Scope
branch incorrectly lowered the right operand to the literal string `file`.
Consequently a macro called inside `for scope in [wsl, ...]` uploaded to the
endpoint cwd's `file`, not the macro argument `~/.codex/AGENTS.md`. An existing
directory at that accidental destination produces the remote directory error;
the ordinary WSL localhost-proxy warning is unrelated to that rejection.

The fix resolves the right operand through the existing `resolve_namespace`
unqualified path flow before emitting typed Select. Bound parameters, raw home
spelling, local projection, and quoted filenames then follow the same rules as
static endpoint selection; no runtime source interpretation is introduced.
The pre-fix compiler also executes `(scope)::file` correctly: parentheses route
selection through `resolve_select`, providing an immediately usable workaround.
Rebuilding the pre-fix source reproduced the reported directory error with a
cwd `file/` fixture and successfully copied both intended home files using this
workaround.

`macro_loop_scope_selects_argument_paths_in_source_object_and_ir` in
`tests/normal_user_model.rs` exercises the supplied macro and nested loops using
the production archive/shell transport with separate local/endpoint homes. It
checks both files and changed bytes across source, source-free object, and IR
execution, preserves an unrelated `file/` directory, and checks quoted and
unbound literal names. The Unix shell launcher is a transport harness, not an
actual Windows WSL acceptance test. Focused standard-library and scope-returning
function journeys also pass, as does strict Clippy for the binary and changed
integration target.

## Native declarations as the typed host interface (v0.7.2)

`native` is the source-to-host interface rather than another family of parser
keywords for individual operations. For example,
`native contains(value, pattern) = "string.contains"` obtains its result and
parameter capabilities from the host signature registry; an optional written
result type must agree with that registry. A lexical alias or ordinary namespace
selection reaches the same registered operation. The implementation name is an
identity in that registry, not a shell command, dynamic library path, or source
fragment to evaluate.

`NativeParam::accepts` is shared by source lowering and executable-object
signature validation. A rejected argument is reported at that argument with its
inferred type and the required capability, rather than a generic whole-call
failure. Path-compatible parameters accept String and Path; actual filesystem
operations project strings through the captured context, while lexical path
operations retain raw spelling. Native effects and result types continue into
the existing typed Native/RangeSource IR, negotiating `native_std_v1`; no extra
source interpreter or second bytecode representation is introduced.

Dependent range operations keep their compiler-owned specialization signatures:
their result depends on the input item type and callback, so they are not
misrepresented as a single fixed scalar FFI signature. This is a typed capability
distinction, not a source-spelling exception.

## Engineering basis

The public filesystem surface follows the operations and value distinctions in
the [C++ filesystem working draft](https://eel.is/c++draft/filesystems), retaining
msync's endpoint-aware paths and ordinary atomic-copy/retry semantics rather
than adding a transactional filesystem layer. Typed native capability tables
follow the separation of host implementations from source bindings used by
[Rust intrinsic declarations](https://doc.rust-lang.org/nomicon/ffi.html).
The source/primitive boundary follows the usual staged-compilation principle:
source composition specializes into a small typed executable IR, and host-only
operations remain explicit primitives rather than being reparsed at execution.

The research connection is the explicit typed IR boundary, not dynamic source
interpretation: [A Typed Intermediate Representation for Dynamic Languages
(2026)](https://doi.org/10.1145/3811921) makes otherwise implicit dynamic behaviors
visible to static analyses. Here source specialization is intentionally smaller:
no JIT, dynamic reflection, or general polymorphic runtime is imported; native
effects and unbound endpoint provenance are explicit typed nodes. Source
compositions disappear into existing operators and lazy range plans.
