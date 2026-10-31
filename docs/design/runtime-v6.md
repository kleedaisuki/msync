# Structured control runtime (0.6)

## Execution boundary

`hir::control` adds a serializable typed expression graph and nested `Region`s,
not source text and not a second compiler in the interpreter. Constant-only
expressions retain the existing static lowering path. Every `Control` instruction
is a singleton basic block. The interpreter drains admitted finite work before
evaluating that instruction; filesystem observations join only overlapping flow
destinations. It never prepares an unselected arm or polls an unused range. Explicitly released external programs remain governed by
the existing process acquire contract rather than being implicitly joined.

`exists` is an observation at its actual interpretation point. `Let` snapshots
its result in a session-owned typed slot. A scoped expression `Let` restores its
previous slot value after evaluation, including on error. Boolean `and` and `or`
evaluate their right operand only when necessary. Named-source diagnostics attach
at the control dispatch boundary, so errors raised through nested `?` paths retain
owned source provenance.

This is the same structured-region pattern used by [MLIR SCF](https://mlir.llvm.org/docs/Dialects/SCFDialect/):
branch regions are explicit, operands/results are typed, and loop exits carry
values. It does not imply speculative permission to inspect filesystem operands.

## State and repeated bodies

The runtime owns `SlotId -> Value` state across REPL submissions. Compiler-created
slots are unique in their session and object linkage rebases them. Type-preserving
bool/string reassignment uses `Set` on an existing runtime cell; ordinary immutable
values remain compile-time values. A loop writes
its iteration slot, interprets one selected body, and consumes the nearest break.
Value-producing loops accept bool, string, path and space values; a missing value
is an error rather than an invented sentinel. Open-ended loops check cancellation
and cooperatively yield each iteration; there is no arbitrary execution-count cap.

Each invocation freshens process identities defined directly in its body, then
remaps descendant references to those identities. Nested bodies freshen their own
launches only when selected. Runtime-generated identities descend from `usize::MAX`
and never enter a relocatable object, whose serialized process identities remain
bounded by the existing object contract. This prevents repeated launch sites from
reusing an already instantiated child or capturing the previous iteration's port.
Completed body-local processes retire on lexical exit; released live children
remain runtime-owned until settlement.
Dynamic program launches carry typed executable, argument, and cwd expressions.
Only the selected launch evaluates them; strings remain argument bytes and paths
remain endpoint-aware arguments. The cwd must evaluate to a local path. Runtime
launches reuse the ordinary `admit_program` owner, scheduling flags, process ports,
and implicit acquire behavior. Dynamic launches participate in the same per-body
process freshening and object process dominance rules as static launches.

Ordinary dynamic transfers are materialized as one ordinary typed transfer and
re-enter the existing publication interpreter; no separate filesystem-copy engine
is introduced.

## Lazy range admission

A range plan contains an unevaluated source plus adaptor stages. `Cursor::next`
polls upstream only when a downstream consumer needs another value. Therefore
`missing/<.+> | views::take(0) | ranges::empty` never enumerates the missing directory.
Filter/transform/take/drop/while/stride/unique stages evaluate only demanded items;
`any_of`, `all_of`, `none_of`, `contains`, and find consumers short-circuit. Reverse
is an explicit buffering boundary because the last item cannot be produced from
a forward directory stream without consuming its input.

Directory names are enumerated once when the source is first polled and sorted
with UTF-8/Unicode lexicographic order to preserve deterministic selector behavior.
This metadata-name snapshot is intentionally distinct from eagerly evaluating
predicates or acquiring file contents. The implementation reuses standard Rust
iterators and the existing `regex` dependency; adaptor state is explicit because
predicates are asynchronous filesystem expressions, not synchronous closures.
The contract follows [C++ range adaptor laziness](https://learn.microsoft.com/en-us/cpp/standard-library/range-adaptors?view=msvc-170),
while excluding integer-oriented adaptors from path/string-only ranges.

Element consumers `first`, `last`, `find`, and `find_if` fail on empty/no-match;
there is no nullable or invalid-path sentinel in the static value type system.

## Filesystem effects

`control_fs` reuses existing endpoint resolution and cancellable WSL/SSH command
transport. Native Local/Env endpoints and remote WSL/SSH filesystem endpoints are
supported. PATH is readonly: existence is supported, deletion and directory
iteration are rejected. HTTP/TCP are not filesystem namespaces for these effects.

`exists` follows symlinks, returns false only on not-found (including a dangling
link), and reports access errors and symlink cycles. `delete` inspects the entry
without following its final symlink and unlinks a regular file or symbolic link;
it never recursively deletes a real directory and rejects special stream entries.
Remote helpers receive safely quoted arguments, bounded outputs, and cancellation
using the existing transport owner. Failures retain the backend/path and chained
cause rather than being collapsed to `false`.

## Object and structured-IR contracts

The version-2 `MSYNC-OBJECT` envelope remains compatible with the existing `0.5`
operation feature set. Objects containing typed control or bool/dynamic/range
exports declare language `0.6` plus `typed_control_v1`; a `0.5` payload cannot
smuggle in a control operation. Older static objects retain their encoding and
validation requirements rather than being silently reinterpreted.

Control expressions carry typed semantic IR, never source ASTs. A path expression
stores its symbolic relocation directly; object construction clears the stale
physical path, and instantiation binds it using the same HOME/ENV/module/cwd
rules as ordinary operands. Recursive traversal includes loop expressions,
function-local expression bindings, callbacks, nested regions and exported
ranges. `ResolvedSpace` represents an explicitly contextualized/frozen namespace
and requires absolute logical context paths. No validation or binding step tests
filesystem existence.

Object validation checks expression annotations against inferred types, bool-only
conditions, same-typed bool/string reassignment, prior slot definitions, lexical
callback slots, homogeneous path/string ranges, positive strides, valid regexes,
and loop-result agreement. A 128-level bound limits semantic recursion, and each flat range pipeline is
limited to 128 stages before lazy cursor construction. Ordinary
operation identities/origins and relocations are checked across nested regions,
not just the top-level vector. Stream consumption is merged across exclusive
arms; definitely available process identities are intersected across arms and do
not escape loops. A separate set preserves global process-definition uniqueness.

Instantiation freshens both process and runtime slot identities, including slots
inside expression-local bindings, callbacks and exported expressions. This lets
multiple instances share one REPL/runtime session without aliasing scalar cells.
The recursive freezer used by structured IR handles nested operations too.

`tests/object_v6.rs` exercises relocated execution after a preceding write,
conditional nested outputs and deletion, repeated-instantiation slot freshness,
forged annotations/slots/features, branch-only process dominance, and unchanged
legacy object feature handling. Existing `object_codec_v4` and `object_linker_v4`
remain targeted compatibility gates. Their initial selected run passed 6 and 9
tests respectively; the new end-to-end gate is owned by the integration run.

Dynamic program launches use typed `Control::Spawn` operands (path executable,
string/path arguments, local path cwd), so a loop element can be passed as an
argument without serializing source or deferring type checking. These launches
participate in object process symbols, branch dominance, loop ownership and
fresh-identity remapping exactly like ordinary static launches. Their embedded
paths use the same inline relocation and recursive IR freezing machinery.

Dynamic transfers keep port ownership explicit: `TransferInput::Scalar` differs
from a typed process `Port`, and `TransferOutput::File` differs from `Stdin`.
Only scalar/file arms contain expressions. Object validation and instantiation
therefore preserve linear stdout/stderr/stdin claims and fresh process references
without encoding a port as a filename or a string scalar.
