# Independent review: interprocedural value provenance

2026-11-13. Bounded source/actual-VM review of the uncommitted Core candidate in
[the design note](native-value-provenance-v090.md). No production implementation
was edited by the reviewer. These controls are not full frontend/VM acceptance.

## Reviewed identities and publication boundary

Local ValueIDs are finite instruction/entry identities; foreign allocation-site
regions are may-origins, not singleton runtime objects. Current function index
must come from the program table, not equality of function-code records. Every
imported expression needs an identity that is finite *and* preserves the actual
operand environment. Reusing an original callee ID while storing only the first
actual operand is not a sound renaming operation.

`ProgramFacts.Solve` serializes solving/public queries under its Gate, and the
candidate freezes functions only after the global change iteration closes. This
is the right publication direction. It is insufficient if a demanded callee was
never registered, or if expression identities grow on every iteration. A reentry
guard avoids stack expansion; it does not prove either condition.

The reviewed `NativeValueFlow` explicitly preserves the pre-NextIterator state
on its failure edge and defines the new item only on success. Bind changes the
location edges without changing saved pre-Bind pointer nodes. Foreign fields and
allocation-site memory use union/weak updates, not store forwarding between
different dynamic objects at one source site. These source-level observations
are positive checks, not claims that all interprocedural substitutions are proved.

## Causal controls and actual findings

Initial frozen sources are in `.temp/native-provenance-review/source/` with a
before/after/copied SHA256 manifest. The later 185-input snapshot in `final/`
matches these Core candidate hashes:

```text
NativeAddressAnalysis.cs
581efcc3394172248a092a6f62be38ed14e4a7a7bcf2df2c7562791d438381ee
NativeAddressAnalysis.Calls.cs
97b87de1258462101d79d9756e97a5741a9af311be3f78dfeac5d31419c7c61c
```

The fixtures construct ordinary NativeProgram values, pass strict
`NativeArtifact.Validate`, run the actual Off interpreter, and compare actual
values with the separate analysis result. No alternative evaluator supplies the
runtime oracle. Earlier harness setup failures (default source glob and omitted
Store.C initialization mode) remain separate logs; they are not product failures.

### 1. Two field calls require distinct actual operands

```text
field(x)       = x.child
wrapper(a, b)  = { u = field(a); v = field(b); return v; }
main           = wrapper(outer(child-layout-1), outer(child-layout-2))
```

Both outers have one identical physical Scope-reference field. Child layout 1
contains a Number field; child layout 2 contains a String field. The actual VM
returns child layout 2. Restoring only the four old Canonical-based interner keys
gives `{1,2}`; actual-operand keys give `{2}`. Thus this particular control proves
lost correlation/precision, **not** the initially suspected underapproximation
to layout 1 alone. The reviewer did not relabel it as a reproduced unsound proof.
Receipts: `oldkeys-result.json`, `fixedkeys-result-3.json` and
`final/direct-result.json` under the review directory.

The subsequent pure-formal normalization was also source-reviewed. Its final
predicate excludes Top, unknown layout/elements, concrete targets/foreign
regions/callables/elements/retention/layouts and all other symbolic uses. It
matches the formal and layout-use role of the entry node. All four interners
then store the same normalized operand used by the key. That is materially
stronger than normalizing only the key while retaining a first arbitrary payload.

### 2. Equal code records are not equal program indices

Use one identity-function code object at both `Functions[1]` and `Functions[2]`:

```text
p = FrameAddress(local0)
Call function1(p)
result = Call function2(p)
return result
```

The old constructor found the first equal record. After index 1 was registered,
requesting index 2 constructed another analysis associated with index 1, which
was not added to the owner's worklist. Its uncomputed return remained empty.
The actual VM returned the current frame pointer, while the analysis published
`MayAliasCurrentFrame=false`, with no targets. This is a real unsound false-NoFrame
result, not only a missed optimization. Calling index 2 first would hide the bug.

The explicit-index candidate rerun reports `MayAliasCurrentFrame=true` and one
frame target, with the same actual VM result. Evidence:
`fixedkeys-result-3.json` (failure), `final/direct-result.json` (fixed). Existing
static pointer-metadata consumers still using an ambiguous function-record
constructor should not be repurposed as semantic proof APIs.

### 3. Stack reentry prevention does not bound higher-order expressions

The following is six instructions plus a one-instruction identity function:

```text
rec(f, condition) {
    if condition { return f; }
    return rec(f(f), condition);
}
```

The Native IR validates. Asking the generic owner for a fact at the base return
does not close within the explicit 20-second negative-test limit on the final
snapshot. Actual invocation `rec(id, true)` should take the immediate base return;
it cannot even reach execution while this demanded graph is unresolved.
`final/recursive-result.json` records the bounded failure with a 512 MiB managed
heap cap; no facts were accepted after the timeout.

A separate copy changed **only** Console count tracing and an explicit review
stop after twelve rounds, not keys/transfers. Invocation count stays one, while
application-node counts are:

```text
round:          1  2   3   4   5   6   7   8   9   10   11   12
applications:   2  6  12  20  30  42  56  72  90  110  132  156
```

Every round reports changed. These first twelve observations fit `n*(n+1)` and
expose imported symbolic-expression expansion with a fixed invocation count.
The trace completes in about 0.61 seconds. The review cutoff is instrumentation,
not a proposed production depth/iteration limit. Evidence:
`instrumented/result.json` and `instrumented/delta.json`.

Use stable original IR producer/use IDs plus finite call-site roles and joined
operand payloads, yielding cyclic constraints. Do not intern successively nested
evaluated terms as new definitions. Recursive projection/read transport must
also be checked with `walk(x,c) = c ? x : walk(x.next,c)`; fixing applications
alone is not a proof that every symbolic constructor has a finite identity space.

### 4. CopyMemory must contribute a real destination write

A 13-instruction, non-recursive validated program initializes
`A.child=child-layout-1` and `B.child=child-layout-2`, performs one outer-layout
`CopyMemory(A <- B, count=1)`, then loads `A.child`.

The actual interpreter returns layout 2. The reviewed final analysis instead
returns the singleton proof `{1}`. This is a real false physical-layout proof:
CopyMemory/ClearMemory had only populated escaping roots, while the new
effect-based clobber walk started from effects and saw no destination write.
The retired function-wide MemoryBarrier clobber had concealed this omission.
Evidence: `extra/copy-result.json`; fixture: `CopyMemoryCase()` in
`extra/Program.cs`.

The required repair is producer/effect propagation or invalidation of the actual
destination's compatible cells, including exported aliases. A VM layout guard
cannot compensate for publishing an invalid compiler fact. The Core owner has
implemented the destination effect and is combining it with the finite-identity
repair; the final combined candidate still requires the independent rerun below.

## Required closure before accepting the next snapshot

* Rerun the two actual-VM correlation/index controls on the final immutable Core
  and Runtime copy, not merely on an intermediate patch.
* Close the higher-order negative and the recursive field/read negative without
  cutoffs, first-operand interning or missing callback/alias effects.
* Rerun CopyMemory and relevant ClearMemory/callee-export cases: an unresolved
  or widened proof is safe, but it must not exclude the actual returned layout.
* Keep external demand serialized until the demanded component is complete;
  missing/unregistered summaries and reentrant imports must not look like proved
  NoFrame or a closed physical layout.
* Preserve caller-owned versus callee-owned activation identities, value/location
  capture substitution, weak site updates and iterator success/failure controls.

All review artifacts remain inside the repository. The fixtures and exact source
hashes distinguish observations from current-owner fixes. The source owner owns
production changes and their integration; this review does not certify the new
graph merely because the five real record projections now have useful facts.

## Final combined rerun: bounded review closed

The subsequent candidate was copied once into
`.temp/native-provenance-review/cyclic/source/`: 185 Core/Runtime/settings files,
verified before/after/copied against the owner's four-file manifest, and verified
unchanged after running. The independently built probe reports zero warnings.

```text
NativeAddressAnalysis.cs
859dbbab66dd4ad1d63dbb15eaed620905c320ca1c65951c7d6c960e390b8385
NativeAddressAnalysis.Calls.cs
703bcd291e3636a86166c97cf52fa853db27354fc78f3674f267fced92f9cf36
```

| Independent actual-VM control | Final observation |
| --- | --- |
| Two field calls, return second child | Actual layout 2, proved alternatives `{2}` |
| Shared code record, Call1 then Call2 | Actual frame pointer, MayAliasCurrentFrame true, one local target |
| CopyMemory replaces child referent | Actual layout 2, no false singleton proof (alternatives unresolved/null) |
| Higher-order recursive application | Analysis closes; `rec(id,true)` returns function 1 |
| Recursive field/read transport | Analysis closes; `walk(node,true)` returns the same address |

The four fresh processes each completed in approximately 0.39--0.47 seconds,
including startup and actual execution. These timings are bounded-control
observations, not a throughput benchmark. The Source IR's generic recursive
branches remain in both tests; the successful base invocation is not an excuse
to skip their analysis.

The application fix uses finite original producer/use/call-site roles with owned,
joined callee/argument operands. It does not turn a newly evaluated expression
into another source definition. Copy/ClearMemory now contributes its actual
destination to the effect/write authority. Strict pure-formal normalization uses
the same admitted operand in keys and payloads. These changes close the specific
counterexamples above without adding a VM guard or production iteration limit.

Receipt: `.temp/native-provenance-review/cyclic/acceptance.json`. The corrected
probe's hash and unchanged production-source hashes are recorded together in
`validated-source-manifest.json`; an initial fixture constructor compile error
and its corrected build remain separate logs. No live reviewer process remains.

**Bounded sign-off:** no reproduced blocker from this review remains in this
snapshot. This is not a general termination theorem for every symbolic
constructor, independent execution of all concurrency/platform tests, or
end-to-end performance acceptance. Keep the old negative receipts; the original
higher-order timeout, false-NoFrame and false-layout observations were genuine
and are not replaced by the final positive results.

### Follow-up: visited frontier and changed-fact propagation

The owner subsequently corrected shared-DAG revisitation and late Input changes.
The independent reviewer reran **the same five controls**, without extending the
fixtures, against the owner's coherent `core-set-import` Core/Runtime assemblies
and these exact source hashes:

```text
NativeAddressAnalysis.cs
0196e8107d293d4a6265c9385f147966d6fcaa1cdb63f9e76d9b35d8bf9d5f4e
NativeAddressAnalysis.Calls.cs
8232626c05adc3e3c17a5e8db22e97e7ccf7e13777c81f7ccaf0fe0c27fbced2
```

All five pass with the same actual values and proof results as the prior table;
source/payload hashes remain unchanged. Receipt:
`.temp/native-provenance-review/dag-final/acceptance.json`. This confirms those
controls survived the traversal repair. The owner's late-formal/capture/element,
inverse-registration and shared-diamond controls are additional owner evidence,
not independently rerun claims here. In particular, this receipt does not relabel
the preserved 602-input source-Set timeout as an independently repeated success.
