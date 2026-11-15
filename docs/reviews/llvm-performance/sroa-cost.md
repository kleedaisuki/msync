# SROA compile-cost and pinned-source review

Reviewed current managed SROA, its containers/layout helpers, the pinned SROA.cpp,
and the existing midend JSONL. No production edits, builds, or timing runs.
Pinned LLVM: a55e64432a516fd6507d3ecfba7407cfe425f940.

## Assessment

One concrete translation-specific excess-work defect is worth repairing first:
`OrderedSet.ExceptWith` reverses upstream subtraction's driving collection.
The other opportunities below are allocation/representation improvements, not
incorrect SROA algorithms. I found no executed speculation loop, runaway
requeue, or missing struct-layout cache in this profile. None of these findings
currently attributes the measured telemetry-off +4.738 ms to a particular site.

The 31.098 ms recorded SROA observation is especially unsuitable as a direct
estimate of recurring SROA work: **13.167 ms occurs in one alloca span, of which
only 0.046 ms is its three direct child spans**. The unaccounted interval is in
the slice-construction region, but the receipt does not identify why it elapsed.
It is not evidence for a specific algorithmic or GC diagnosis.

## Ranked findings

### 1. P2 — subtraction scans surviving worklists even when deleted keys are absent

**Locations:** `src/Msync.Optimization/Llvm/LlvmSroa.Data.cs:168`;
caller `LlvmSroa.cs:122–126`; deletion population
`LlvmSroa.Driver.cs:156–183`.

`ExceptWith` always traverses every value using `RemoveAll`, constructs a
capturing predicate, then performs a second set subtraction. After an original
aggregate alloca has been replaced by partition allocas, the deleted original
is normally absent from `PromotableAllocas`, while its newly created partitions
are present. Each such deletion still scans every accumulated candidate;
remaining Worklist entries are scanned too. This survives telemetry-off.

The pinned source has the same three subtraction calls (SROA.cpp:6606–6608),
but its container's `set_subtract` iterates the **removed keys** and calls
`remove`. `remove` membership-tests first and scans its vector only on a hit.
Thus absent original allocas cost a set lookup, not a traversal of all surviving
promotables. Primary source:
[SetVector.h at the pin](https://raw.githubusercontent.com/llvm/llvm-project/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/ADT/SetVector.h),
methods `set_subtract` and `remove`.

**Evidence/scale:** confirmed cost-model divergence. The existing profile has
94 analyzed original allocas, 69 new partition allocas, and 133 promotion
candidates. `<main>` alone has 44 analyzed, 63 new, 81 promotable. It does not
record actual subtraction comparisons, so their exact number and wall-time
contribution remain unknown. The profile is small enough that this is not by
itself a credible explanation of the entire regression.

**Fix/experiment:** translate `set_subtract` directly: `foreach` removed key,
call existing `Remove`, which already checks `set.Remove` before `values.Remove`.
Preserve insertion order. Root can count removed-key probes and actual vector
removals for the existing midend journey, then reuse the telemetry-off paired
compile experiment. Do not replace ordered worklists or reorder promotion.

### 2. P2 opportunity — presplit pays a heap-backed set per use on the all-unsplittable workload

**Locations:** `LlvmSroa.Presplit.cs:33–57`;
`LlvmSroa.Splitter.cs:61–86` (overlap lookup).

Every partition constructs a `Dictionary<Instruction, HashSet<Slice>>`; each
ordinary load/store instruction inserts its one slice into a new heap-backed
HashSet with its own backing storage. In the supplied midend run, **all 290
slices are unsplittable**; all 133 partitions take the
`presplit.unsplittable_or_contained` branch and no presplit candidates are
recorded. Consequently the maps/sets and overlap queries cannot produce a
presplit result on this run.

This is faithful algorithmic traversal, but not faithful small-container cost:
pinned SROA.cpp:150–151 uses `SmallDenseMap<..., SmallPtrSet<Slice *, 8>, 8>`;
the per-instruction slice set has inline capacity rather than allocating a hash
object and backing arrays for a singleton. The managed implementation has 290
single-slice sets in this run: only load/store slices are recorded, each such
instruction has one memory pointer operand, and there are no intrinsic slices.

**Evidence/scale:** certain allocation/work path; profile presplit scope totals
87 invocations, 0.551 ms and 177,240 bytes. These are inclusive, telemetry-on
observations, not a measured recoverable 0.551 ms or an explanation of +4.738 ms.

**Fix/experiment:** retain the overlap algorithm while storing a first slice
inline in the dictionary value, allocating overflow storage only for genuinely
multi-slice instructions. An even narrower equivalent elimination is to avoid
presplitting when the existing slice vector contains no splittable slice:
every iteration necessarily continues before appending a candidate. Preserve
work/reason telemetry rather than hiding the skipped decision. Compare graph
and output for the retained overlapping-copy presplit properties as well as the
real midend source; then pair the existing compile journey once after the
coherent change. Do not exclude pointer allocations from SROA globally.

### 3. Lower-priority representation opportunity — trivial partition decisions allocate scratch collections

**Locations:** `LlvmSroa.Types.Vector.cs:116–143`;
`LlvmSroa.Rewriter.TreeMerge.cs:15–38`;
`LlvmSroa.PartitionRewrite.cs:57–95`.

Vector viability constructs a candidate List and two `OrderedSet` objects
(each owning a List and HashSet), then candidate-array/enumerator/closure
machinery. Tree-merge constructs two Lists before seeing the scalar first
store and rejecting. Partition rewriting similarly creates empty PHI/select
sets and result Lists even for the simple scalar case. Pinned equivalents use
local/inline small vectors and sets.

**Observed decisions:** all 133 vector queries have no candidates (266 recorded
no-candidate rejections across the two checks); all 133 tree-merge attempts
reject at `store_shape`; there are no PHI/select speculation queries. These
are genuine compiler allocations, unlike runtime NativeDatum struct movement.
Their own allocation attribution is not recorded, so they should not displace
finding 1 or be described as a measured hotspot.

**Remedy:** preserve the source decision sequence and use lazy scratch
allocation/small representations at the rejected paths. In particular, tree
Lists need backing state only after the first eligible partial access. Do not
introduce a separate optimizer, Native type eligibility gate, or scalar-only
algorithm. A single grouped allocation observation after the representation
change can discriminate this from the instruction-creation costs.

## Excluded and bounded hypotheses

- **No worklist/speculation explosion:** 94 analyzed, 133 partitions, 290 slices;
  no `partition.allocas_requeued_for_speculation`,
  `partition.new_allocas_requeued`, presplit requeue, or speculation counters.
  All 133 partitions are immediately promotable; the observed run has no
  problematic post-promotion revisit cascade.
- **No missing struct-layout memoization:** `LlvmDataLayout.cs:185–188` caches
  `LlvmStructLayout` by actual struct identity. Scalar size/alignment queries
  are direct switches; caching all of them is not justified by this evidence.
- Stable slice sorting matches the source ordering. Its LINQ temporary arrays
  (`Data.cs:43–47`) are extra managed allocation, not a discovered sorting
  semantic defect; blindly changing to unstable `List.Sort` would lose fidelity.
- Aggregate load/store preprocessing before escape checking and scalar
  load/store recreation before promotion match upstream `runOnAlloca` and
  rewriting. Their presence alone is not a translation error, and replacing
  SROA with a mem2reg eligibility gate would not implement the requested pass.
- Type selection's two candidate checks, integer viability, partition iterator
  split tails, and source-default PreserveCFG follow the inspected source.
  I found no source-vs-port decision mistake in these compared paths.

## Profile interpretation and next experiment

Source: `.temp/llvm-sroa-midend-tier-aot/O2/compile.jsonl`.
Only final `optimizer.pass.llvm-sroa` metrics were summed for work counts;
alloca/split/delete spans expose the same cumulative per-function dictionary
and must not be summed as work counters.

Selected observations (each row independent; nested rows must not be added):

| Span | Calls | Inclusive time | Inclusive allocation |
| --- | ---: | ---: | ---: |
| optimizer.pass.llvm-sroa | 33 | 31.098 ms | 7,105,968 B |
| optimizer.llvm-sroa.alloca | 94 | 24.447 ms | 4,347,992 B |
| optimizer.llvm-sroa.split | 87 | 5.637 ms | 1,901,680 B |
| optimizer.llvm-sroa.rewrite-partition | 133 | 4.266 ms | 1,376,544 B |
| optimizer.llvm-sroa.presplit | 87 | 0.551 ms | 177,240 B |

Outlier: alloca span `000000000000022c`, `<main>`, source.start 9,
13.1667 ms, 32,896 B. Children: aggregate-accesses 0.0014 ms,
split 0.0440 ms, phi-select 0.0006 ms. Therefore its large elapsed interval
cannot be assigned to partition rewriting, presplitting, or speculation.
If root needs further attribution, add a slice-build span/cause counters on
this actual path rather than treating the entire alloca elapsed time as
recurring slice complexity. The narrow first product change remains the
upstream `set_subtract` translation; assess it against the authoritative
telemetry-off nine-pair compile result, not this one profiled launch.

## Implementation follow-up: removed-key subtraction

The central worklist implementation now translates pinned
`SetVector::set_subtract` directly: enumerate the removed-key collection and
call `OrderedSet.Remove` for each key. `Remove` membership-tests its hash set
before searching the ordered vector. Surviving worklist order is unchanged;
an absent deleted original alloca no longer triggers a scan of its surviving
partition allocas. No SROA eligibility/type/rewrite choice changes.

This follow-up is a source-cost correction, not a measured attribution of the
compile regression. It has not been independently built or timed by its author;
the root owns the focused source/presplit journey and consolidated build.
