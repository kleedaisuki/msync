# Experimental Scope field inline cache

This is a frozen-source experiment, not an enabled product feature. The product
source tree is never patched. `field-cache.patch.py prepare` archives the exact
research baseline and creates control/candidate source trees under root `.temp`.
Its generated candidate adds a two-entry cache per executed field PC. A cache
hit requires `ScopeLayout` reference identity and reads the current field through
`ScopeValue.GetField`, including its shared Cell. Successful existing one-based
field hints retain the original fast path and do not create a cache.

The cache resolves a member name to an offset; it never retains a Scope instance
or field payload. A third shape takes ordinary named lookup. Generation refresh
clears all site state before new typed code/constant projection is selected.
Both cache capacity and the experimental implementation are optimization policy,
not restrictions on source objects.

## Workloads and checks

The principal source workload selects between two Scopes with the same field
names in different orders inside an actual Range loop. O2 keeps a `GetMember`;
one layout satisfies the compiler hint while the other exercises a legitimate
hint miss. The probe records the actual residual operands and constructor
layouts. A same-order source is the existing-hint negative control. A version
with A=0 models old/unhinted typed artifacts, not a claim that new source normally
omits hints. Tiny, literal-return and real 4 KiB atomic-output controls include
cache setup and all ordinary interpreter work.

`--check` additionally executes captured-field mutation at one repeated site,
selected missing-member fallback after layout replacement, codec round trips,
and a deferred REPL callback after a new function/code generation. It must run
against both snapshots before timing. There is no new machine-code generation.

## Reproduction

From the repository root:

```text
python3 tests/Performance/AdaptiveInlineCache/field-cache.patch.py prepare
python3 tests/Performance/AdaptiveInlineCache/field-cache.patch.py control
python3 tests/Performance/AdaptiveInlineCache/field-cache.patch.py candidate
python3 tests/Performance/AdaptiveInlineCache/measure.py --control .temp/runtime-adaptive/inline-cache/control-native/msync-inline-cache-probe --candidate .temp/runtime-adaptive/inline-cache/candidate-native/msync-inline-cache-probe --output .temp/runtime-adaptive/inline-cache/measurements
```

Publishing is serial; do not overlap timed runs with another benchmark/build.
`measure.py` alternates AB/BA processes, retains all within-process samples,
records image SHA-256 and requires identical optimized input shapes. Allocation
is measured outside timing with `GC.GetTotalAllocatedBytes`. Every measured VM
operation creates a fresh interpreter, so first-use plan allocation is included;
this is not a cache lookup microbenchmark. Timing excludes source compilation,
which only constructs and verifies the same typed program outside the interval.
The separate startup/CLI decision remains a different workload.

Reusable conclusions belong to the parent runtime-adaptive validation note.
Raw source/native manifests and samples stay under `.temp/runtime-adaptive`.

## Native result: defer adoption

Frozen source: `887fc6230fa26bd8ed4c9ae39ff76471d78f8b99`. Linux x64 Native AOT
control SHA-256 `18a0002be6be6433ce472542682f9004e155dfbf7daff393f22f687b1d6d881d`;
candidate `948d74a67f519a3743c09cc80d5679ad12c81493b894af91efd473c91b480f35`.
The source archive, prototype files and each image have manifests under the
experiment's `.temp` directory. No product files were modified.

Three alternating process pairs, nine samples per process (27 observations per
case), three warmups, 4,096 consumed loop iterations:

| Case | Control p50 / p95 ms | Candidate p50 / p95 ms | Control / candidate B per VM operation |
| --- | ---: | ---: | ---: |
| Real source, reordered layout/hint miss | 13.263 / 22.574 | 12.803 / 24.728 | 6,116,832 / 6,117,120 |
| Real source, successful existing hint | 12.819 / 22.176 | 11.670 / 22.432 | 6,116,456 / 6,116,464 |
| Unhinted artifact (A=0) | 11.837 / 21.693 | 11.800 / 21.673 | 6,116,832 / 6,117,120 |
| One-iteration source | 0.0096 / 0.0122 | 0.0096 / 0.0176 | 6,520 / 6,528 |
| Literal-return executable | 0.0012 / 0.0032 | 0.0012 / 0.0040 | 1,608 / 1,616 |
| Actual 4 KiB atomic output | 1.477 / 2.364 | 1.710 / 2.801 | 14,784 / 14,792 |

The source workload genuinely contains one retained O2 `GetMember`, with hint 2,
left layout `[other,item]` and right layout `[item,other]`. The successful-hint
control has hint 1 and matching layouts on both edges. Thus neither workload is
an expression statically erased before the cache can execute.

The candidate's apparent 3.5% source improvement is not persuasive causal
benefit: the successful-hint control, which never creates a PIC, improved more,
and the deliberately unhinted artifact was effectively unchanged. Tail latency
also did not improve. File-control timing is variable and is not credited as a
cache benefit. Cold state costs 288 bytes on the PIC cases and the interpreter
field adds 8 bytes even on non-PIC cases. The loop still allocates about 6.1 MB;
field-name resolution is not shown to dominate this interpreter workflow.

**Decision: defer this two-entry cache.** The correctness model is viable, but
these actual O2 cases do not justify adding its state/invalidation machinery to
the product. Existing successful hints remain the preferred fast path. Raw data:
`.temp/runtime-adaptive/inline-cache/measurements/summary.json` and six per-image
reports; all image pairs require identical source-generated opcode/shape inputs.
An independent reviewer also accepted 14 raw-IR/codec falsifiers with three
reordered layouts, warmed missing-member failure/source span and live shared
Cell mutation. These are separate semantic evidence, not extra timed samples.
