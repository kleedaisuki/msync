# 0.8 language and execution architecture

## Scope and ownership

The 0.8 migration replaces the indentation-based Rust frontend with a C#
compiler and a Native AOT executable. The original 0.7.3 implementation is
preserved, independently buildable, under [`legacy/`](../../legacy/MIGRATION.md).
Its source and artifact semantics are not changed by the new parser. Published
tags and asset bytes are never rewritten.

The root build uses .NET 10. The implementation boundaries are:

| Component | Owns | Must not own |
| --- | --- | --- |
| `Msync.Core` | Semantic types, values, typed bytecode, module/stage contract | Source parser, authenticated sessions |
| `Msync.Compiler` | Tokens/AST, binding, type inference, refinement, specialization, lowering | Executing filesystem observations or processes |
| `Msync.Runtime` | Artifact codec, IR optimization, contextual binding, native operations, interpretation and transfer lifetime | Parsing/debug-source execution |
| `Msync.Cli` | Commands, REPL, diagnostics, LSP transport and native process entrypoint | A second implementation of source semantics |

Compilers, interpreters and native registries share semantic types rather than
guessing capabilities from identifier spelling. Standard-library source modules
are compiler inputs, not a second prose-only API.

```text
*.msync
   -> tokens / syntax tree
   -> inferred typed operations + specialized function identities
   -> relocatable bytecode
   -> machine-independent optimization and control/effect blocks (-O level)
   -> *.mso
        -> contextual binding + runtime lowering of selected work
        -> *.mexe
             -> interpreter / native operation admission / execution
```

The artifacts contain operations, types, constants, symbolic context and source
locations, not source to evaluate later. Source locations are diagnostic data.
Relocatable artifacts capture no live authentication, filesystem snapshots,
handles or optimizer proofs about a previous execution. Executable artifacts
freeze context, not remote credentials or a claim that external files still
exist. Loading an artifact does not authorize reusing stale observations.

## Language decisions

* Braces define expression blocks. A trailing expression supplies the value;
  `;` discards a value and an empty/discard-only block evaluates to `null`.
  Whitespace and indentation do not establish syntax structure.
* `let` introduces an immutable lexical binding; another `let` may shadow it.
  This deliberately avoids adding an unrequested assignment/lifetime model.
  `_` discards a value without introducing a nullable binding.
* `Null` is a real type and `null` a value, not `false`. Null tests use `is` /
  `is not`; boolean conditions still require Bool. A nullable binding requires
  written `?`, and `value?` means **is null**, following the user's example.
  Tests refine the same binding in the appropriate control-flow branch.
* `fn` unifies the old macro/symbol/boolean distinction. Specialization is keyed
  by function identity and argument types, not source substitution. A recursive
  specialization reserves its identity before lowering its body: recursion is
  a normal typed call edge, not an infinite demand for inlining. Closures retain
  definition-time lexical values; parameter expressions execute once.
* Scope is a lexical member object. Connection identifies a physical endpoint.
  Both use `::`, but member lookup and endpoint path qualification are different
  typed operations. Anonymous functions/scopes use exactly the named forms'
  value representation.
* Paths retain raw text, captured local projection and explicit endpoint
  provenance. Requalifying an unbound `~` uses the selected endpoint's home.
  Lexical path operations do not accidentally turn raw text into local absolute
  strings. Path/String conversion follows the consuming operation; a bare
  String transfer source remains literal bytes, preserving the existing useful
  contract rather than guessing from file existence.
* Match is exhaustive over the inferred input domain. Regex patterns inspect a
  path's native string projection. Type patterns operate on semantic types.
  `decltype(expr)` performs inference only; even native/process/transfer
  expressions in its operand are not executed.
* The omitted-but-existing Bool type remains available. Numeric range/count
  workloads require a Number primitive; it is not a special source-string
  convention for native range calls.
* Removed syntax (`:=`, binding `=>`, indentation declarations, async/await and
  the delete statement keyword) is not reintroduced through frontend aliases.
  Native filesystem deletion remains a callable library operation.

## Effects, scheduling and compatibility

Control-flow edges and effect dependencies are separate concerns. Runtime
admission belongs to selected work: a possible SSH operand in an unselected
branch must not connect, prompt or globally join unrelated operations. Unknown
effects prohibit only transformations needing their independence. Static
optimization must not pre-execute filesystem queries, transfers or programs.

The ordinary-copy contract is unchanged: an owned same-directory temporary file,
atomic replacement, at most one file fsync, and cleanup by the live owner.
Completed members of a failed directory/range copy may remain; report the actual
outcome and permit retry. There is no persistent rollback journal, before-image,
startup scan, parent-wide publication lock or filesystem-wide syncfs.
One finite remote transfer uses one native SSH client/channel for metadata and
bytes, preserving configured aliases, agents and native prompts.

The old Rust CLI remains the explicit compatibility boundary for old scripts
and old object/IR formats. New binary headers distinguish both format version
and language semantics. A recognized container cannot silently change the
meaning of its operations. Native AOT distribution must run without an installed
.NET runtime; `.mexe` is msync executable IR, not an OS-native executable.

## Engineering and research basis

* [Microsoft Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  makes the distribution self-contained and disallows runtime code generation.
  Use a static opcode interpreter and statically reachable native registry, not
  Reflection.Emit or runtime-loaded compiler assemblies. Runtime-specific
  optimization means contextual lowering and execution choices, not a hidden JIT.
* [Rust's monomorphization collector](https://rustc-dev-guide.rust-lang.org/backend/monomorph.html)
  separates discovering concrete callable instances from code generation.
  Adopt identity-based specialization and retain recursion as a graph edge.
* [MLIR bytecode versioning](https://mlir.llvm.org/docs/BytecodeFormat/)
  distinguishes stable container encoding from evolving operation semantics.
  Apply that distinction to msync's own compact format; a dependency on the
  LLVM/MLIR runtime is not needed for this DSL.
* [Fallin and Bernstein, PLDI 2025](https://doi.org/10.1145/3729259)
  derive whole-program compilation through partial evaluation of an interpreter.
  The relevant lesson is one operational semantics across stages, not a promise
  of adopting their C/C++ compiler technique directly in Native AOT C#.
* [Pečimúth et al., ECOOP 2025](https://doi.org/10.4230/LIPIcs.ECOOP.2025.25)
  reuse optimized IR with verified specialization assumptions and relocation.
  Keep persistent machine-independent operations separate from live observations
  and runtime-owned identities; do not persist the latter as portable proofs.

## Integration acceptance

The key acceptance journey is source -> compile `.mso` without effects -> remove
source -> execute -> bind `.mexe` -> change cwd -> execute with captured context.
Use real filesystem bytes and real child processes. Incremental probes should
cover null refinements, exhaustive pattern/type matching, generic/recursive
closures, scope ownership, finite selectors, ordinary replacement, endpoint
selection and authentication ownership. A native published binary must run those
same journeys; managed-build success alone does not establish Native AOT
compatibility. Cross-platform installation evidence supplements local checks.

Implementation-specific language, runtime, tooling and validation findings belong
in their focused 0.8 notes. This document fixes cross-component decisions, not a
claim that every acceptance journey has already passed.
