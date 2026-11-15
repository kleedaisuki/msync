# LLVM available-load queries

The source is LLVM revision `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`llvm/lib/Analysis/Loads.cpp`, `AreEquivalentAddressValues` (287–304),
`isSafeToLoadUnconditionally` (456–539), and available-load scanning (554–852).
`LlvmLoads` implements the actual local queries, not a heap-wide identity model.

Unconditional-load queries first consume shared dereferenceability/alignment
facts and the source sanitizer exclusion. Failing that, they scan backward in
the context block for an ordinary same/equivalent-address access of sufficient
size/alignment. Potentially freeing writer calls stop that scan; lifetime calls
have the source exception. Volatile accesses do not establish regular memory.
The four sanitizer function flags are ordinary source attribute carriers.

Available-load lookup preserves source atomicity/access-size and cast-legality
rules, including elementwise atomic vector access sizes, constant larger stores,
and constant memset byte patterns/offsets. The BatchAA overload first searches
for a candidate and only then checks collected intervening writers. It retains
source budget six and its zero-budget behavior. The iterator overload instead
uses source zero-means-unlimited, optionally skips proven nonalias stores, and
retains its iterator position at candidates or restores it at clobbers/limits.
The managed iterator is the next instruction, with null denoting block end.

Counters belong to the caller's optional metric dictionary: query/scan counts,
actual AA work, candidate kind, source limit and clobber decisions. No global
cache, stopwatch or disabled-recording container is introduced. The only local
collection is lazily allocated when deferred AA has an actual intervening write.
The source has no represented debug/pseudoprobe instructions to skip here.

`AvailableLoadProperties.Run()` exercises actual IR load/store/atomic/memset
forwarding, speculative access facts, sanitizer behavior, deferred AA clobbers,
and both source scan-budget/iterator contracts. The root owns build and combined
execution; this implementation did not launch a separate MSBuild process.
