# v0.6 typed control and specialization

The existing compiler session remains the lexical state owner. Values are paths,
strings, spaces, processes/ports, constant booleans, typed scalar expressions, or
lazy range plans. No new operation substitutes text back into source code. The
old flat transfer/program path remains the normal lowering for static operands;
only genuinely dynamic values require `hir::control` nodes.

## Binding and evaluation time

`=` declares bool/string bindings and reassigns existing bindings only when the
scalar type stays the same. Existing string `:=` remains accepted for backward
compatibility. `:=` continues to bind paths/ranges and `=>` spaces or programs.
Straight-line constant reassignment remains a compiler-local substitution and
folds without allocating a runtime cell. A bool/string binding assigned by a
runtime loop or conditional is promoted to one typed cell before that control
region; subsequent reads use its slot and writes emit `Set`. Both conditional
arms target the same outer cell, while branch-local declarations do not escape.
Macro arguments own local assignment state rather than borrowing a caller's
mutable cell. Pure function reassignment specializes as successive lexical values,
with dynamic initializers captured once by expression-local `Let` nodes.

Boolean/string constant operations fold without touching filesystem state. Both sides of logical operations are statically checked, even when a
constant left operand short-circuits runtime evaluation. `exists` is always a
runtime filesystem observation, not compiler-input IO. Dynamic bindings lower to
one `Let` slot, so reusing a binding observes its captured value rather than
repeating a filesystem query.

`boolean` and `symbol` share bounded lexical specialization machinery with a
checked function flavor. Every function must have a terminal return; boolean
results must be bool. Nested declarations are limited to the enclosing pure
function's flavor. Dynamic function arguments/local declarations become nested
expression `Let` nodes, preserving one evaluation per binding and declaration
order even when a result references a local multiple times. Constant invocations
produce no runtime instruction or expression-let overhead.

## Lexical ownership

Functions/macros capture immutable definition-time maps. A nested declaration
captures ancestor maps followed by current locals, so the nearest declaration
wins. Values, declaration sites and diagnostic expansion traces use the same
merge order. Invocation starts with explicit parameters; `global` imports values
from the merged definition-time snapshot. The caller's unrelated locals are
never implicitly captured. Function registries are definition-time snapshots as
well. `drop` removes the current names from the lexical maps; it neither cancels
processes nor promises resource reclamation.

A shared monotonic slot allocator spans functions, macro expansion, included and
executed scripts. Failed compilations may leave gaps but cannot reuse an identity
that an older runtime or persisted object still owns. Object instantiation
freshens its slots before importing exports.

## Control flow

Conditions require bool, with no truthiness conversions. Both conditional arms
are statically checked. Each arm receives a lexical scope and independent linear
stream-consumption candidate. Constant conditions retain only the selected arm's
instructions and side tables. Dynamic arms union consumed capabilities, while
branch-local pending programs are joined at the lexical branch boundary; program
names do not escape that branch. This avoids awaiting a process identity that
was never spawned in the selected arm.

`for` binds a typed path/string iterator slot and consumes its lazy plan; only a
bare break is valid. `loop` may return bool/string/path/space. All breaks belonging
to one loop, including statically unreachable conditional arms, must agree on
one result type; an expression loop needs at least one value-returning break.
Nested loops have separate break ownership. Runtime expressions contain typed
nested regions, never parser AST or source strings.

The Rust Reference's [loop and break contract](https://doc.rust-lang.org/reference/expressions/loop-expr.html)
informs break ownership and result typing. C++ range view/algorithm separation
informs lazy plans; see [range support and sources](ranges-v6.md). LLVM's
[effects and speculation rationale](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
is the reason a statically known expression shape is not permission to speculate
its filesystem observations during compilation.

## Integration boundaries

Typed control is an optimizer effect boundary. Recursive HIR walkers discover
nested origins, relocations, process definitions, slots and exports. Filesystem
operations go through the existing contextual execution layer so normal path
policy, publication, diagnostics and cancellation remain active. The object
format declares the typed-control feature only for objects that use it; existing
objects retain their established contract.

### Dynamic program and transfer operands

A single program-call lowerer validates executable/argument types, then selects
ordinary `Spawn` when everything is static or typed control `Spawn` when an
iterator, cell or loop result supplies a runtime operand. Both use the ordinary
interpreter program owner, including default await behavior, process diagnostics
and fresh process identities for repeated loop bodies. Working-directory operands
remain contextual paths rather than capturing the compiler process cwd.

Dynamic transfers use a sum type for each role: input is scalar path/string or a
program read port; output is a file path/mode or program stdin. Program streams
are capabilities, never scalar strings. Both forms claim the same session linear
port ownership, and recursive object imports discover both forms. A loop may
consume streams of programs launched inside that loop but cannot repeatedly
consume an outer program's one-shot stream. Loop-local pending programs settle
before break or normal iteration exit; a zero-iteration for does not leak a
phantom process await outside its body.

A relative path fragment in a dynamic join is literal string data plus an
optional explicit endpoint constraint on the Join node. It is not a standalone
relative file location, so object relocation does not accidentally anchor the
suffix. Dynamic path suffixes preserve their actual endpoint and still require
agreement with the base. This separates fragment validation from full-path
contextualization without introducing a second path language.

Explicit `global` also accepts captured prior macro/symbol/boolean bindings.
Separate frozen template snapshots distinguish those imports from local or
forward declarations; `global` cannot silently import a newly declared local
function with the same spelling.
