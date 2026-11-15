# SROA cost reviewer self-statement

I participated as an independent reviewer of SROA compile cost and fidelity to
the pinned LLVM implementation. I did not design or implement the original
SROA port, choose its managed containers, or integrate the default pipeline.
I cannot give those authors' historical intentions as my own recollection.

My assignment was to find concrete extra work or translation mistakes, not
merely say that SROA is more expensive than mem2reg. I started from the supplied
review brief, design notes, frozen image identities, existing measurements and
repaired-cause list. I treated the nine-pair telemetry-off midend compile delta
of +4.738 ms and the separate profiled 31.098 ms SROA observation as different
kinds of evidence. I did not reproduce either timing experiment.

I compared the actual managed alloca driver, slice/partition representation,
presplitting, type selection, rewriting and worklist handling with vendored
SROA.cpp at revision a55e64432a516fd6507d3ecfba7407cfe425f940. I retrieved the
same revision's upstream SetVector.h because its implementation was necessary
to determine whether the managed subtraction retained the source cost model.
I inspected the shared layout and use-list carriers to avoid blaming an
expensive-looking helper without checking its implementation. I also parsed
the existing JSONL with inexpensive command-line probes: final pass counters
for work volume, and parent/child spans for timing boundaries. These methods
were chosen to distinguish actual port-specific excess from necessary source
algorithm work without launching overlapping builds or fresh benchmarks.

My strongest concrete finding was that OrderedSet.ExceptWith scans all
surviving values before subtracting the set. Upstream SetVector::set_subtract
instead iterates removed keys and uses membership checks before searching its
vector. This matters when deleted original allocas are absent from the growing
promotion list. It establishes extra work, not its elapsed-time contribution.
I recommended translating the upstream operation directly using the existing
Remove method, preserving worklist order rather than inventing a new optimizer.

I also identified allocation opportunities in the managed representation:
presplitting constructs per-instruction heap-backed slice sets even though all
290 observed slices are unsplittable; vector viability rejects all 133 queries
for lack of candidates; tree merging rejects all 133 attempts at store shape.
The corresponding upstream small containers carry different allocation costs.
I distinguished these opportunities from semantic defects: executing those
source decision procedures is not itself an incorrect translation. The
reported presplit time and allocation include instrumentation and are not a
promise of recoverable savings.

I ruled out several tempting explanations for this particular observation:
there are no recorded speculation or new-alloca requeue paths, and struct
layouts are already memoized. I did not prove these mechanisms harmless for
all possible programs. I found no decision mismatch in the compared partition,
vector/integer selection and source-default speculation paths; this is not a
full correctness certification of every SROA helper.

The most important limit in my timing interpretation is one 13.1667 ms alloca
span whose direct children total only 0.046 ms. Its unaccounted elapsed interval
is in the slice-construction region, but the recording does not identify its
cause. I did not establish a GC pause, scheduling pause, or pathological slice
algorithm. I also did not measure subtraction comparisons, exclusive container
allocation, or how much of +4.738 ms any proposed fix would recover. Shared
cumulative per-function counters must not be summed across nested alloca spans;
I used final pass counters instead.

My analysis could need correction if the inspected current source does not
match the frozen profiled binary, if an omitted recording path changes the
meaning of absent counters, or if a structural assumption about ordinary
load/store slices does not hold for another input. The container cost-model
difference is directly visible in source, but its priority as a performance
fix remains subject to the existing real-journey comparison after a coherent
implementation change. I made no production changes, builds, new timing runs,
or commits; I wrote review artifacts only.

Any root narrative attributing all 31.098 ms, its difference from old mem2reg,
or the entire telemetry-off compile regression to SetVector/OrderedSet would
exceed my findings. Likewise, my review must not be presented as the original
porter explaining why the system was designed this way. My contribution was
to identify a narrow source-aligned correction, bound several allocation leads,
and prevent an unsupported millisecond attribution.
