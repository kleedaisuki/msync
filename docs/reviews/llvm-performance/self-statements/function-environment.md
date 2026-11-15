# First-person account: Function environment integration repairs

## My assignment and authorship boundary

I was the worker named `fix_function_environment`. My assignment was to repair
concrete failures in the existing source-to-Native integration journeys while
the root agent serialized builds, AOT publication, and benchmark runs. I did not
own the overall optimizer, SROA port, original Function SSA design, or benchmark
methodology. I inherited the three-field LLVM Function aggregate, compact Native
Function ABI, `NativeEnvironment.View`, machine scalar support, and default SROA
integration. I should not be credited with originally choosing those designs.

The root owned the memory, pointer arithmetic, Function-memory bridge, and most
build-time integration changes. Another agent implemented the final shared
`PhiCopy` repair. My durable changes were the target carrier-selection partial
and its minimal hooks, actual-storage environment-schema resolution through the
runtime/preparation paths, and related design documentation. I also supplied
reduced real source reproductions and investigated emitted Native artifacts.
No overall performance improvement can be attributed to my work in isolation.

## What I personally changed and why

### Selecting carriers after scalarization

The actual Range O2 journey failed when lowering a fresh pointer select; the
midend O2 journey failed while preparing a fresh pointer PHI before emission.
These new LLVM values had no source `LogicalType`. LLVM `ptr` alone did not tell
the existing Native backend whether to allocate a code-token register, string
root, address, or nullable tagged carrier.

I added `LlvmNativeLowerer.Carriers.cs`, made `SelectedValueType` use it, and
connected the scalar PHI, select, and ordinary slot-creation paths. The query
uses existing per-use/source ABI hints, selected input slots, incoming SSA
values, allocated storage views, typed pointees, Function leaf shapes, and
existing layout-offset information. It does not emit instructions or mutate
LLVM values. That mattered because PHI preparation precedes ordinary emission,
and constants can be context-uniqued across differently typed uses.

I used per-body successful-query caching and an active traversal set to stop
cycles. This was a bounded target-selection repair, not an alias analysis,
optimization eligibility test, or heap proof. I did not give unknown pointers a
default Scope interpretation. Preserving information on every newly created
optimizer instruction would have been another approach; it would have spread
Native ABI propagation obligations across LLVM transformations, which was not
necessary for the observed target failures.

This query is not a complete general solution for arbitrary heterogeneous
opaque-pointer programs. In particular, consumer hints are trusted, recursion
is cut rather than globally solved, and only top-level successful queries are
memoized. The join logic does not construct a general pointer-pointee union for
incompatible machine or pointer carriers. Those are real limits of my patch,
not properties proven away by the integration tests. I did not separately
measure its traversal cost. The observed journeys established useful coverage,
not completeness or negligible overhead.

### Preserving the actual environment storage schema

A later real benchmark failed when `make_row` was specialized to capture a
Number location. Composition had interpreted its environment using the
original template code's Any capture signature, even though the producer's
allocated environment layout retained a pointer-to-Number field.

I changed production composition to use an image-owned actual-address layout
resolver. `NativeImage` lazily derives and caches the capture schema from the
existing compiled layout field declarations, using the same pointee/location
rule as the older explicit-layout `MakeFunction` path. Its existing layout
identity mapping handles frame-placed interior regions as well as ordinary
heap allocations. Ready composition, pending composition, and contextual
preparation share this resolver. Public environment storage retains its own
schema. I preserved the existing four-argument public array-based Compose
method, rather than silently changing that caller contract.

I chose the existing layout declarations as the authority because they describe
the actual stored locations. Choosing the template signature again would
reintroduce the bug. Recreating capture containers or schemas on each Compose
would add work without changing the underlying data. Adding an artifact
section or identifying environments by field-name heuristics was unnecessary.
The resolver delegate is cached once per image, not allocated per composition.

This solution still depends on the correctness of producer layout metadata and
its existing frame-region identity map. Cache publication uses compare-exchange,
so simultaneous first queries can construct redundant schema candidates before
one is retained. Also, the compatibility array overload and production resolver
overload remain two explicit calling contracts. I preserved that surface for
compatibility, not because it is the cleanest possible new API.

## What I missed or stated too strongly

1. My first carrier patch got the pointer select/PHI paths compiling but did not
   itself materialize a naked `LlvmFunction` as a code-token constant. I flagged
   that subsequent boundary; the root implemented the actual constant fix. It
   would be wrong to call my initial carrier patch a complete Function SSA fix.
2. During the loop-capture investigation, I initially said the other aggregate
   PHI copies were safe because their canonical leaf shapes matched. That was
   incomplete: a leaf can reuse an existing monomorphic producer slot even when
   its declared shape is nullable. The other agent correctly compared actual
   slot physical types for all PHI copies. I corrected my message and document.
3. The root identified the decisive typed-copy mismatch: a raw typed copy from a
   monomorphic Scope address into a Null|Scope union does not insert the union
   tag. I did not originate that fix. My reduced source demonstrated that the
   closure worked inside the loop but failed after crossing its PHI; the other
   agent implemented the shared copy helper.
4. My first actual-schema implementation still passed the unused whole-template
   schema arrays through production APIs. In preparation this needlessly forced
   construction of schemas for every function. The root caught the waste. I
   staged a cleanup patch; the root applied it after the active build finished.
5. I initially wrote a Core method-group call at each production composition.
   The delivered version caches the delegate once per image; I corrected this
   before handing the source over for the build. I should not describe the
   initial draft as allocation-free.

## Evidence and limits of the delivery claims

I personally inspected the actual failure logs, compiled and disassembled
fixtures using the already-built CLI, and ran reduced real source journeys. I
did not run the root's MSBuild or Native AOT publication commands. For the
capture-schema fix, the root reported that its new AOT binary successfully ran
the previously failing bound `candidate.mexe` with the recorded stdin and
matched the benchmark oracle byte-for-byte. I recorded that result with its
ownership; it was not my independent benchmark run.

The distinction matters: the repairs remove demonstrated integration defects.
They do not, by themselves, establish a compiler speedup, optimal Function
representation, comprehensive correctness for every LLVM program, or a reason
to attribute unrelated historical architecture choices to me. I made no product
changes, builds, or new benchmark runs while preparing this statement.
