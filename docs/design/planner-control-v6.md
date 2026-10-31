# Structured control admission in 0.6

A control instruction is an interpretation boundary, not a DAG operation whose
body can be speculatively admitted. `hir::Blocks` emits every
`InstructionKind::Control` as a singleton block. The previous finite block must
settle before its condition, range source or deletion observes the filesystem.
Nested regions are admitted by the interpreter only when the selected branch or
next iteration executes. In particular, preparation of `if exists path` does not
stat the condition path or inspect sources in either branch.

The logical effects layer conservatively marks controls as `unknown_external`.
Even a manually assembled block containing both ordinary operations and controls
therefore receives completion dependencies instead of commutation proofs across
the control. Constant scalar work is removed by compiler folding, so this
conservative treatment does not impose scheduling machinery on folded constants.
Preparation invalidates its host/type state at a control, just as at a native
program launch; it cannot propagate a preceding metadata observation through a
branch or a loop.

Fence normalization treats a control as an observation delimiter, preserving an
explicit acquire/start fence at its entry. It never pulls ordinary transfers out
of a control region or normalizes across the control's boundary. Static plan
output labels the operation with its kind and says that it is evaluated at
dispatch, rather than dumping a potentially large nested expression tree or
pretending to know the chosen branch.

Preparation certificates compare control payloads by structural typed equality,
including nested regions and expressions. A prepared object's identity and
ordinary source metadata alone are insufficient: changing `true` to `false`, a
range predicate or a branch body must invalidate the witness.

The design follows the separation of effect descriptions from speculation
permission in [MLIR Side Effects and Speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
and the typed nested-region model of the [MLIR SCF dialect](https://mlir.llvm.org/docs/Dialects/SCFDialect/).
No new dependency is required: existing Rust enums express the control contract,
and existing `proptest` coverage in `tests/planner_control_v6.rs` checks that every
control remains alone in its block for arbitrary transfer/control sequences.
