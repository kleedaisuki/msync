# Native middle-end integration

Run `dotnet run --project tests/NativeMiddleEnd -c Release`. The executable checks
source, source-text-free HIR and checked native artifact paths at O0/O1/O2/O3.
It does not reconstruct source from lowered artifacts.

The oracles cover every builtin and IR address descriptor through pure static
inlining, both capture modes through the actual environment, post-construction
capture mutation, callable replacement and callee-before-argument evaluation.
Private Process/transfer calls retain their work boundary and publish actual
bytes before the caller advances. CFG controls retain an empty infinite loop's
cancellation behavior and relocate prepared-plan PC intervals. Must-reaching
memory facts preserve an old stored value when its source register is redefined
on one predecessor. Typed-slot pressure includes Move consuming one temporary
while another remains live, across all native kinds. Disjoint closure environment
registers with the same open Scope ABI but different allocation proofs remain
separate, while compatible physical pointers still reuse storage; deliberately
merging the former must remain a strict artifact error.

Binding a location establishes a usable alias; it does not initialize pointee
data. Tests distinguish an unbound register, an uninitialized binding pointer,
a bound-but-uninitialized read with its original span, and a closure that writes
an uninitialized capture before reading it. No dummy read is inserted to satisfy
the validator. A nullable field initialized to Null, read through an inlined
Scope parameter's frame cell and later mutated must retain its initialization;
Null bytes are not equivalent to an uninitialized cell.

Private fixed-frame Load/Store pairs may become ordinary typed Copy operations
without materializing their intermediate addresses. The gate covers all builtin
representations, identity, signed zero, explicit Path conversion and CFG entry
edges. Promotion requires initialized, unaliased source cells as well as private
destinations: Copy is discardable, whereas reading an uninitialized capture in
an otherwise unused Store must still fail. A local closure can recover a frame
pointer without escaping; unknown environment/geometric aliases therefore do
not establish a private-cell proof. Both failure paths retain their source spans.

`tests/NativeExecution` additionally retains real pending-value membership,
callback, process/cancellation and captured-alias controls. Their readiness and
identity contracts constrain value numbering: an ABI of Number alone does not
make two pending computations interchangeable.

For a phase comparison, `--emit SOURCE DIRECTORY` analyzes/lowers once at O0,
then writes O0/O1/O2/O3 portable and prepared artifacts from that same raw IR,
plus `code.tsv`. The typed Linux helper `measure.py` runs two such bound inputs
on one frozen Native AOT executable in alternating order, checks exact stdout
for every invocation, and records payload/artifact hashes, CPU and elapsed raw
samples. Compile/prepare are outside this timed boundary. Coordinate a quiet
window; profile-level allocation/call diagnostics are separate measurements.

`--optimize RAW.mso DIRECTORY` instead reuses an existing relocatable native IR
input, so optimizer revisions can be compared without changing frontend output.
The appended `code.tsv` columns expose frame-address, Load, Store and Narrow
counts; static counts are attribution, not measured execution cost. Do not feed a
prepared executable back into portable optimization.
