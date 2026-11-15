# First-person account: machine-scalar target architecture

## My actual assignment and authorship

I am the contributor named `native_scalar_abi`. My original assignment was an
architecture task: inspect the existing Native model, artifact format, runtime
storage/readiness and LLVM-derived target boundary, then write
`docs/design/llvm-machine-scalar-abi.md`. The assignment explicitly said not to
modify production before agreement. I delivered the initial design document and
messages to the integration coordinator and relevant family owners. I did not
build, benchmark, commit, or edit production code in that task.

The name retained by `aggregate_memory` identifies that architecture assignment;
it does not establish that I authored `MarkInitialized`, the machine-scalar
runtime implementation, Function component execution, or the final integrated
pipeline. The present design document includes a published-foundation section,
implementation descriptions and changed initialization wording added after my
initial delivery. I cannot claim those later edits or the corresponding code as
my personal work, nor do I have the implementation author's full original task
history in this context. I will not assign that authorship to a guessed name.

My contribution was nevertheless not unrelated to the storage change. My initial
proposal contained an overbroad initialization recommendation, explained below.
That part is my own design responsibility even though I did not write the
production helper.

## The problem I was actually trying to solve

Full upstream SROA/InstCombine can produce machine integers and split aggregate
values that the existing Native target could not express. Native Number was
binary64. Its bits could not be treated as an arbitrary i64 value without
changing arithmetic semantics or losing precision. The source Function LLVM
value contained code, environment and native-name pointers; the established
Native Function ABI instead stored an index and two typed roots. Raw numeric
bytes and CLR roots already occupied separate physical regions.

The requested direction was not to add source Integer/Float32 types, capture
modes, a general heap model, an LLVM backend or another optimizer. I was asked to
make optimizer output representable while preserving source behavior and the
existing artifact/execution contracts.

I therefore proposed:

- An internal, exact machine-type alternative, with inline bits for <=64-bit
  payloads and a typed wide payload for larger values. No conversion through
  Number and no generic per-value object box.
- Separate mandatory physical type from optional source type, so a pending i64
  result would not acquire a fabricated Number/Any source identity or force an
  eager call.
- Flatten Function SSA into component slots and pack/unpack at established ABI
  boundaries, retaining the actual environment storage rather than copying
  captured contents.
- Express managed pointer external state through the pinned LLVM DataLayout
  pointer flags and existing upstream SROA queries, rather than inventing a
  custom rejection gate or heap/alias proof.
- Add explicit wire alternatives within existing revision-4 records instead of
  adding a section or silently extending fixed records.
- Distinguish initialized byte fill from existing ClearMemory, which erased
  initialization. Calling the latter memset(0) would be a real semantic error.

These choices addressed concrete representation gaps. They were not evidence
that the integrated path would retain the old backend's cost per ordinary store,
copy or Function projection.

## My own initialization-design mistake

My original document said that a byte store initializes its written bytes and
that a scalar read should require its full read extent, rather than only the
first byte. I associated arbitrary scalar byte views with byte-level
initialization tracking. I did not restrict that proposal to an explicitly
supported raw-view boundary or distinguish it sufficiently from the existing
source-field readiness contract.

That was an overgeneralization. Representing an optimizer-produced i64 bitcast
requires exact bytes; it does not by itself require unrelated ordinary Number,
Path, Function and Address stores to write every payload byte's readiness flag.
Nor should LLVM padding/undefined bits become a new runtime initialization
proof obligation. A trusted compiled scalar load is not a source admission
boundary. The current design wording correctly separates those concerns.

I did not implement a full-width read check, and the current code does not
uniformly maintain or consume the full-width invariant that my original prose
suggested. But the fact that no scan was ultimately added does not make the
original suggestion cost-free: it provided an unnecessarily broad rationale
for widening write-side maintenance. I should have specified which operation
consumed that maintenance before recommending it.

The independent storage review now identifies actual broader initialization
writes in ordinary helpers, including `MarkInitialized(...).Fill(true)`, while
ordinary readiness consumers and explicit ReadMachineScalar inspect the view's
starting flag. Raw generated tier paths still maintain one flag or omit it
where already allowed. These facts come from current source and the review,
not a timing run I performed. I cannot tell from my retained history exactly
which prose or message the implementation author used when making each edit.

## Costs and assumptions I failed to close

### I focused on representability without budgeting ordinary-path work

The architecture correctly avoided enlarging the universal transient carrier
and boxing ordinary numbers, but that did not exclude other common-path costs.
I did not trace each additional physical-type query, generic machine branch,
source-type projection or readiness update through the already-hot ready-source
store/copy paths. My initial implementation order treated the shared foundation
as a coherent slice without specifying that source-only operations should retain
their selected ordinary path whenever the compiled type already distinguishes
it. A design can preserve field sizes and still introduce millions of extra
uses of existing helpers.

The useful requirement is not simply “machine operations have a fast ready
case.” It is also “adding an internal machine type does not make every ordinary
source value pay for raw-machine semantics it does not use.” I should have made
that boundary explicit at the outset.

### Flattening aggregates is not a completed runtime cost model

Component SSA avoids allocating a tuple object for every aggregate operation,
and retaining an environment address avoids copying capture contents. Those
are design advantages, not proof that a generic FunctionComponent handler is
as cheap as the old dedicated environment-address path. I did not own the
later handler or projection implementation. My proposal still needed to call
out the additional datum movement and dispatch at that interface instead of
leaving “pure bit/reference movement” sounding equivalent to negligible work.

### Artifact compatibility did not establish execution parity

Preserving existing IDs, source alternatives and fixed records matters for
external contracts. It does not establish Native AOT code quality or bound
execution performance. My report was documentation-only and did not claim a
benchmark pass; nevertheless, stopping at a complete representation proposal
left the ordinary-path performance assessment to integration. That was an
unresolved engineering cost, not something correct status wording solved.

## What I can and cannot conclude from the current review

I accept that my initialization prose was too broad and failed to justify the
extra work by an actual consumer. The current review supplies a concrete target
for removing needless maintenance from the dominant compiled source path while
preserving any exposed raw-view behavior. It does not establish the elapsed-time
contribution of that maintenance, and I cannot assign it the entire reported
compile or runtime regression. I did not produce the paired timing receipts.

The review also rules out hypotheses that should not be repeated as my rationale:
new typed root alternatives did not enlarge NativeValue/NativeDatum instance
fields, <=64-bit machine values were not implemented by double conversion, and
ordinary Number values were not newly boxed. None of those exclusions excuses
the extra storage work.

My narrow recommendation is to separate ordinary source-field readiness from
explicit raw-byte-view readiness at the compiled path/boundary, retain existing
public raw/interior-view behavior, and remove maintenance where no consumer
requires it. Merely leaving ReadMachineScalar's method body unchanged would not
by itself preserve a public sequence that writes a Number and reads an interior
machine view. The integration owner needs that distinction, not another global
initialization analysis or safety validation pass.

## Corrections for the coordinator's final account

1. Attribute the original target architecture and its overbroad initialization
   proposal to me; do not attribute production MarkInitialized authorship to me
   solely because `aggregate_memory` remembered my task name.
2. Do not present the present design document's published-foundation section as
   proof that I implemented those production files. Later shared-document edits
   and my original architectural contribution are different records.
3. Do not describe exact machine bits, CLR roots or pending physical typing as
   an invented source safety feature. The avoidable cost was broadening their
   maintenance onto unrelated ordinary paths without a demonstrated consumer.
4. Do not convert a source-level extra write into a measured percentage or use
   opcode totals as a MarkInitialized invocation count.
5. Preserve the actual source/runtime/public API contracts while removing work;
   neither a runtime proof framework nor suppressing upstream optimization is
   the remedy I recommend.

For this personal account I read the existing storage review, the current design
note and the relevant current storage code. I changed this self-statement only:
no product edits, builds, timing runs or commits.

## Related records

- [Machine-scalar target design](../../../design/llvm-machine-scalar-abi.md)
- [Independent scalar/storage review](../scalar-storage.md)
- [Storage reviewer's own account](storage-review.md)
- [Aggregate-memory contributor's account](aggregate-memory.md)
