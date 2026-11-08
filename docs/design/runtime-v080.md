# C# typed bytecode and execution (0.8.0)

## Representation and ownership

`Msync.Core` owns the type/value algebra and stack bytecode contract. A module
contains typed function signatures, a constant pool, source spans, and numeric
local/function references. Functions are specialized by the compiler; closures
capture values and carry a function-table identity. Calls never send source or
syntax trees to the interpreter. Recursion uses specialized forward references.
The `Null` value is distinct from Bool. Iterator state, live processes, handles,
transport sessions and lazy enumerators cannot be embedded as constants.

The binary container has independent format version **1** and language-semantics
version **0.8.0**. It is not JSON or serialized source. The decoder validates table
references, layouts and reachable stack/control-flow shape before execution.
Bytecode dataflow rejects stack underflow, mismatched join heights, uninitialized
locals, invalid iterator ownership, invalid closure layouts and impossible
operand-kind combinations. No arbitrary instruction or filename quotas exist.

`Artifact`, `Optimizer`, `Binding` and `Interpreter` are separate APIs:

```
source -> typed specialization -> relocatable Module (.mso)
                                  | pure constant/branch folding, block formation
                                  v
                         symbolic optimized bytecode
                                  | bind captured cwd/HOME/source context
                                  | resolve fixed host builtins, specialize local I/O
                                  | compact no-ops and relocate jump targets
                                  v
                         executable Module (.mexe)
                                  | actual selected operands and filesystem kinds
                                  v
                    dependency scheduling -> native operations
```

Relocatable source execution takes the same binding/lowering route as `.mso`.
Executable modules keep captured context when run from another directory.
Context binding does not authenticate or inspect remote endpoints. Raw path
spelling remains available to lexical standard-library operations; actual local
I/O prefixes the captured cwd without erasing native `symlink/../file` traversal.
`PathSemantics` chooses separators from endpoint plus platform, not host alone:
a backslash remains a POSIX filename character, including SSH/WSL on Windows.

## Execution and effects

The interpreter is async stack bytecode, not a JIT: Native AOT disallows runtime
IL generation. Pure constant folding handles boolean operations, arithmetic,
string concatenation and type identity. Basic blocks split at control-flow and
effect boundaries; successors and effect flags are inspectable in `plan`.
Machine binding specializes provably local transfers to a dedicated local fast
path and resolves static connection roots. Other operands remain late-bound.

Discarded scalar transfers may run concurrently. Each admitted transfer records
its source-read and destination-write resources; overlapping read/write or
write/write work retains program order. Local resource identity resolves existing
filesystem aliases. Remote work on the same endpoint is serialized unless its
independence is established, without remote preflight/authentication. Calls,
filesystem observations, branches, loops and return drain the admitted frontier.
`Jobs` bounds work; the configured payload budget additionally bounds copy slots.
This permits the dominant independent-copy workload without guessing that an
opaque process or native mutation has no effects.

A consumed `->` yields Bool. Operational I/O/permission/native-process failures
yield false, preserve source/destination/cause diagnostics, and increment failed
transfer accounting. Cancellation and malformed typed operations do not become
false. Failed work is visible to CLI exit status even if language code handles
the false result. Completed directory/cohort members remain in place for retry.

Range plans use `IAsyncEnumerable<Value>`. Native adaptors and selectors poll
only on demand. `MakeLazyRange` stores already-pure values directly and explicit
zero-argument closures for effectful elements; a compact mask distinguishes them.
`DeferRange` invokes a range-producing closure on first demand, including its
arguments. The source compiler therefore does not spend effects on `take(0)`.
A REPL session retains local values; lazy values keep their originating compiled
module/context alive rather than looking up a later process cwd.

Path calls return a completed Process value with exact argv, captured stdout,
stderr and exit code. Remote calls share the native SSH/WSL command builder with
file transport, including aliases, SSH config and endpoint-native HOME expansion.
The optional combined output-spool budget applies to bytes before text decoding.
Optional runtime observations are per execution; disabled observation does not
construct event records. Native failure and transfer/process lifecycle events
retain actual outcomes rather than generic error labels.

## Verification

`tests/Msync.Runtime.Tests` is an executable contract harness: 100 deterministic
Unicode binary round trips, pure-fold equivalence, branch-target compaction,
source/object/executable cwd ownership, read-after-write and symlink-alias
scheduling, Bool failure + existing-target preservation, Process stdout and actual
spool budget. The current checkpoint passes **221 assertions**. Run with isolated
build outputs when other agents are compiling:

```
dotnet run --project tests/Msync.Runtime.Tests/Msync.Runtime.Tests.csproj \
  --artifacts-path .temp/build-runtime -m:1 -nr:false
```

Native and transport journeys are documented beside their executable harnesses;
CLI language/source-free artifact journeys belong to the integration suite.
Neither an SDK build nor a shell-backed launcher is evidence of real SSH
authentication or native Windows acceptance.

## Engineering and research basis

- [Native AOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  require avoiding runtime code generation/reflection-dependent serialization.
  Explicit opcodes and binary tables satisfy that deployment constraint directly.
- [MLIR bytecode versioning](https://mlir.llvm.org/docs/BytecodeFormat/) separates
  container encoding from dialect semantics. The same distinction motivates the
  independent format/language versions here; this project does not embed MLIR.
- [Jones, Gomard and Sestoft, Partial Evaluation and Automatic Program Generation](https://raspi.itu.dk/people/sestoft/pebook/pebook.html)
  describes specialization as separating known inputs from residual work. Here
  type/function specialization is static, contextual path/endpoint lowering is
  binding-time, and filesystem facts remain selected runtime inputs. This is a
  concrete staging boundary, not permission to evaluate effects during `check`.
