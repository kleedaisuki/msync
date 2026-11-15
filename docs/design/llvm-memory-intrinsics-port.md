# Standard LLVM memory intrinsic integration

## Source and representation

This port follows LLVM commit `a55e64432a516fd6507d3ecfba7407cfe425f940`.
The source reference is `vendor/llvm-reference/llvm`. These are standard LLVM
calls, represented by ordinary `LlvmCallInstruction`, never project opcodes:

| Source | Product responsibility |
| --- | --- |
| `include/llvm/IR/Intrinsics.td` memory, lifetime, invariant and fake-use declarations | `LlvmIntrinsic.Memory.cs`: declaration identity, overloaded types, immediate arguments, parameter capture/access attributes and memory effects |
| `include/llvm/IR/IntrinsicInst.h` `MemIntrinsic`/`MemTransferInst`/`MemSetInst` | `LlvmMemIntrinsic.cs`: a view over actual Call argument Uses; no second operand owner |
| `lib/IR/IRBuilder.cpp` memory and lifetime factories | `LlvmTransformBuilder.MemoryIntrinsics.cs`: source factories without an invented memory-operation IR |
| `lib/Analysis/MemoryLocation.cpp` `getForArgument`, `getForDest`, `getForSource` | `LlvmMemoryLocation.Intrinsics.cs`: exact represented source cases and Native disabled-libc TLI |
| `lib/Transforms/Utils/PromoteMemoryToRegister.cpp` `isAllocaPromotable`, `removeIntrinsicUsers`, `PromoteMem2Reg::run` | Mem2reg eligibility, lifetime reset insertion and droppable-use removal before instruction-index scans |
| `lib/Analysis/ConstantFolding.cpp` `ConstantFoldScalarCall1` | `LlvmConstantFolding.MemoryIntrinsics.cs`: launder undef/poison and caller-dependent null folding |
| `lib/IR/Instruction.cpp` and `Instructions.cpp` volatility/effects | Call volatility and source memory-effects integration |

At this pin `llvm.lifetime.start/end` take **one pointer argument**. They do not
have the historical size argument. `llvm.strip.invariant.group` is not declared
and is not fabricated. `llvm.launder.invariant.group` is speculatable but accesses
inaccessible memory: it is not readnone and must not be treated as an ordinary
optimizer identity. At machine-code emission its pointer representation is
unchanged, unlike its optimizer state.

`memcpy`, `memcpy.inline`, `memmove`, `memset`, and `memset.inline` use the ordinary
four operands `(destination, source-or-byte, length, volatile)`. The volatile
argument is immediate. Copies have destination-write/source-read capture-none
attributes; only memcpy variants have noalias parameters. Callsite alignment is
kept on the actual call. The façade excludes atomic memory families, which need
their own represented source classes before they can be consumed.

The pinned MemoryLocation source switch does not list `memset.inline`; its
location size therefore remains before-or-after-pointer, rather than silently
adding a new inference. `getOrNone` similarly does not classify calls; callers
use explicit destination/source/argument APIs. Native TLI disables all libc
functions, so ordinary names do not acquire invented C library contracts.

## Mem2reg consumer

Lifetime, droppable and fake-use users do not prevent promotion. Before any
LargeBlockInfo scans, all selected allocas are processed together:

* Drop individual droppable Uses through the Core source helper; preserve the
  assume instruction and its other operands/bundles.
* Insert a promoted-type `store undef` before either lifetime start or end when
  the alloca has load/store users, preserving alloca alignment and source span.
* Erase the lifetime/fake-use calls. No type is needed for otherwise dead allocas.

This is the source lifetime meaning in scalar promotion, not a heap proof or
alias-identity constraint. A real optional assumption cache is threaded to the
existing source poison and phi simplification queries. The old public signatures
remain available; four-argument overloads accept the cache.

Telemetry records declaration reuse/creation and intrinsic creation through the
existing metrics, plus the `optimizer.llvm-mem2reg.intrinsic-users` phase and
`intrinsic_users.dropped_uses`, `lifetime_resets`, and `removed` decisions. The
recording-disabled path creates no telemetry dictionaries.

## Target ownership and byte-storage distinction

`LlvmNativeLowerer.MemoryIntrinsics.cs` implements the marker/launder target
cases. Launder forwards its actual argument slot and operand ABI without changing
optimizer effects. Invariant start yields an undef descriptor (Native chooses
null), not an alias to the memory region; end and lifetime markers emit no code.
This follows the pinned [SelectionDAGBuilder source](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/CodeGen/SelectionDAG/SelectionDAGBuilder.cpp#L7363).


Registry support does not redefine Native memory semantics. Existing
`NativeOp.ClearMemory` clears initialization state and GC roots in addition to
bytes. It is **not** LLVM zero-fill, which initializes bytes. Existing typed
`CopyMemory` copies raw storage, managed-reference roots and initialized spans;
it models exact matching typed regions, not arbitrary LLVM byte slices.

Native scalar/raw-memory work therefore owns real initialized-byte fill and
byte-copy operations. Overlap-correct move must use overlap-correct storage
copying. Numeric subregions after SROA need machine-width integer/floating byte
offsets, never a double carrier or a declared-field lookup. Managed-reference
regions need their actual static allocation layout and reference slots; an
arbitrary partial mixed-reference byte copy is a target representation question,
not a reason to add a global alias/heap solver, reject source SROA, use ClearMemory,
or pretend a live operation emits nothing.

Focused checks in `MemoryIntrinsicChecks` exercise actual Calls and Uses,
parameter attributes, volatile effects, destination size, and the full
mem2reg journey from lifetime/fake-use/assume users to a scalar undef return.
