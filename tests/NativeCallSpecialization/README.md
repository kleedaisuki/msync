# Closed native callable domains

Run `dotnet run --project tests/NativeCallSpecialization -c Release`.

The 77 checks cover O0/O2/O3 source, relocatable roundtrips and executable
roundtrips. They consume the public Buffer API, captured native declarations,
Native-to-Native and Native-to-source replacement, nested callee/argument effects,
REPL relinking and the original user call site's native error span.

The compiler already accumulates the callable alternatives of mutable locations.
At O2/O3 a standalone compilation can lower a location whose complete source
domain is one registered native function to `NativeCall`. Every source location
must agree; unknown or multiple alternatives retain ordinary dispatch. Persistent
sessions and editor probes remain relinkable and do not acquire a closed-world
assumption from their current submission.

A temporary compiler-only boundary marks the end of callee evaluation. The
renderer discards the selected native value there, leaves argument evaluation in
its original order and emits the direct native operation. These markers never
reach artifacts. No argument spill slots or new bytecodes are needed, and no
function-owned scheduling boundary is removed: the original call already selected
a native function, not a source function frame.

Buffer is an owned Range, not a Scope containing one Range. Its named family
holds native function values for primitive operations. This removes a wrapper
Scope and fields array per buffer and a source method frame per primitive access,
while keeping `Buffer::get(buffer, index)` and the fixed element schema.
`Buffer::new` and `empty` remain eager. Snapshots freeze membership while keeping
the ordinary identity of retained elements.

The public Buffer program has two same-index gets separated by a different-index
store, and two size reads separated by a value-only store. Its O2/O3 entry has one
get, one size read and both stores. The indexed-memory pass's separate tests own
alias, CFG and first-failure proofs; this test closes the real source-family path.
