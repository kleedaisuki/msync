# C# typed bytecode and execution (0.8.0)

## Representation and ownership

`Msync.Core` owns the type/value algebra and stack bytecode contract. A module
contains typed function signatures, a constant pool, source spans, and numeric
local/function references. Functions are specialized by the compiler; closures
capture values and carry a function-table identity. Calls never send source or
syntax trees to the interpreter. Recursion uses specialized forward references.
The `Null` value is distinct from Bool. Iterator state, live processes, handles,
transport sessions and lazy enumerators cannot be embedded as constants.

The binary container has independent format version **2** and language-semantics
version **0.8.0**. The reader also accepts format 1; its original path constants
have no additional local spelling. It is not JSON or serialized source. The decoder validates table
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

### Symbolic paths and local native spelling

An unbound Path carries source-owned `Raw` spelling, captured cwd/home anchors,
and an optional `LocalSpelling`. A source `/` join remains `/` in the symbolic
spelling. On Windows its local projection uses native separators, so a binding
of `~/.codex` can be used locally as `~\\.codex` and later qualified onto SSH/WSL
as `~/.codex`. Qualifying before or after that binding yields the same remote
Path. Qualification discards the local spelling and local anchors, not arbitrary
backslashes in the source. A literal POSIX filename such as `left\\` still joins
to `left\\/leaf` remotely; a global backslash replacement cannot express this.

`PathSemantics.JoinValue` and `Qualify` are shared by the VM and executable
lowering. Native Path-to-String conversion, regex matching and local I/O consume
the local spelling; generic lexical inspection and remote operations use Raw.
Derived path operations use `WithRaw` so a changed pathname cannot retain the
previous path's local projection. Windows lexical operations and directory
iteration derive both spellings together; POSIX joins allocate no second string.
The public four-argument constructor and deconstruction remain available.

Format 2 adds the optional spelling to serialized Path values, including nested
scope values and function captures. Format 1 remains readable without guessing
or rewriting its existing path bytes. The retained binary fixture was produced
by the original frozen 0.8 format-1 native CLI from stdin, so it contains no
machine-local diagnostic path. `tests/PathProjection` checks simulated Windows
grammar on every host, physical projected I/O, nested format-2 round trips,
format-1 loading, and source/object/executable qualification. The native Windows
platform journeys separately execute the same language-level distinction.

## Execution and effects

The interpreter traverses the actual selected BasicBlocks using a precomputed
PC-to-block lookup, not a flat source replay or planner-only block annotation.
Each selected effect block lowers runtime-known local transfer operands to the
local executable operation; jump/loop/return outcomes select the next block.
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
independence is established, without remote preflight/authentication. Opaque calls, demanding callback/range calls, filesystem observations, branches,
loops and return drain the admitted frontier. Immediate pure native capabilities
without Range/Function parameters do not globally join unrelated work.
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
module/context alive rather than looking up a later process cwd. Deferred call
resolution memoizes the resulting range on first demand, so repeated consumers
do not reevaluate the source call's arguments/body.

`RangeValue.Selection` retains `PathSelection` origin (directory, .NET regex,
recursive traversal and composed native adaptor projection). A deferred range's
`Resolve` exposes that origin only when selected work demands it. The transfer
owner discovers names, runs the same .NET regex/adaptors/callbacks and sends its
chosen paths over one native SSH duplex channel, then receives the archive on
that channel. A session-scoped query service lets filesystem predicates use the
same selected channel rather than separately authenticate. Zero-demand `take(0)`
does not resolve its source, enumerate or authenticate. See the real native SSH
fixture beside `tests/Msync.Transfer.Tests`.

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
spool budget, lazy `take(0)` and lazy values escaping a REPL submission. The current checkpoint passes **234 assertions**. Run with isolated
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
