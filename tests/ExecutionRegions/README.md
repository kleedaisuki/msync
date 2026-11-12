# Bound execution-region import

Run `dotnet run --project tests/ExecutionRegions -c Release` from the
repository root. Optional `.mso`/`.mexe` paths also check every function
in already generated corpus artifacts. These are projection tests, not
guest execution through the new region representation.

The importer uses the existing typed CFG to retain canonical PCs and
both outcomes of a conditional, even when they share a target PC. Each
reachable block receives fresh parameters for its canonical input stack
positions; each edge passes the producer values in order. The first
forward predecessor in reverse postorder establishes a block's input
depth; every later predecessor, including a loop backedge, must agree.
Unknown operations, underflow and incompatible merges reject only this
optional import. `IteratorNext` defines its value on the yield edge and
preserves the old stack unchanged on exhaustion. An edge to the code end
retains the optional implicit return value.

An operation's explicit inputs are its consumed stack suffix in push
order, including the callable before its arguments. The remainder of
the original stack is an immutable prefix. Each new value or duplicated
occurrence adds a single persistent stack node, so even a deep stack does
not cause a full snapshot per operation. `CopyStack` reconstructs the
canonical bottom-to-top order before an operation or on an edge. It does
not reconstruct local cells, active iterators, plan submissions, pending
results or frame ownership: those remain canonical and are required from
the eventual execution or continuation backend. In particular, a
`Transfer` may conditionally fuse its following `Pop`; it is marked for
canonical dispatch and is not a separately executable region operation.

The suite verifies actual source and standalone `.mso` → bound `.mexe`
round trips. A real Heap program has a nonempty sift-down merge: its two
incoming edges provide different value IDs, and the branch consumes the
selected block parameter from its before-op recipe. The ordinary VM
independently returns the expected score on a sorted sequence. A separate
format-6 fixture joins two real Scope layouts with different field ordinals:
the imported `LoadField` consumes the joined receiver, retains its exact
projection and reads the correct value on both choices. Further controls
exercise iterator state, ordered call operands, duplicate value identity,
implicit return, and rejected malformed stacks.
