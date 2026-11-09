# Bounded specialization-key experiment (0.8.1)

## Question and controlled input

Excessive BTree compilation cost needs a phase-level explanation before another
large run. This experiment asks whether specialization keys already expand a
shared semantic graph before a new executable function is built.

The only input was:

```msync
let tree = $std::containers::BTree::new(0, "");
$std::containers::BTree::insert(tree, 0, "item-0");
```

The host calls `Compiler.Analyze` only. It does not lower, optimize, interpret,
enumerate runtime data, or perform the program's I/O.

## Source mirror and instrumentation

The disposable workspace is `.temp/hir-finite-trace`. Compiler C# sources were
copied there; its project references the frozen9 Core, HIR and Optimization DLLs
directly and embeds the repository standard sources. No product source was
modified by the experiment, and no product Core/HIR/Optimization project was
built. Both mirror builds completed with zero warnings and errors.

The original source-copy receipt records source HEAD
`68a2673196970264839b756ddf3d567cb5d1d18a` and a SHA256 for every copied source
and embedded standard source. This is a current-source mirror, not a claim that
its Compiler DLL is byte-identical to frozen9's Compiler DLL. The dependency
assemblies are fixed:

| Dependency | SHA256 |
|---|---|
| Core | `9722dee738be3650ba76a902916a25d8d0f18128794360ce671ba70bb453965e` |
| HIR | `7c0efe9092db571816906ebf11a1aff023e65ee74d7d8baea7e73cb7949473c7` |
| Optimization | `251dfff5250df06512a2aa3f93fecfaebea97fe28c5b8d32b2e630100cb1944c` |

Mirror-only hooks surround `InstanceKey` and the `BuildInstance` cache miss.
They record template ID, publish/query phase, nesting chain, key length/hash,
arena table counts, caller object/resource counts, and shallow argument/capture
descriptors. `AppendValueKey` records 4-Ki-code-unit crossings. Entire keys and
semantic graphs are never printed.

The original run stops at 80 instance-key entries, a 16-Ki-code-unit unfinished
key, or 20,000 global append visits. The comparison run stops at 40 entries with
the same key/visit guards. A Python supervisor adds a 20-second timeout and a
512-MiB managed-GC heap hard limit. These are experimental stop conditions, not
language restrictions or product quotas. The Python executable used was
`/home/moesegfault/.python/bin/python3`, version 3.15.2. The scripts and mirror
are development-only and introduce no product Python dependency.

## Observation before graph backreferences

The first 18 completed keys were 14–851 UTF-16 code units. Entry 19, the
top-level insert invocation, reached 16,411 code units before completing its key.
It had not reached its cache lookup or `BuildInstance` miss hook.

At entry 19:

| State | Count |
|---|---:|
| Functions / templates | 9 / 49 |
| Types / Scope layouts / Scope constructions | 1154 / 30 / 32 |
| Caller objects / indexed resources | 9 / 5 |
| Tree argument fields | 8 |
| Two captured Scope field counts | 30 / 12 |

The trace reached global append visit 4,956 at the stop. That is a global count,
not the number of visits attributable solely to key 19. Repeated Function
descriptors were visible during the key's 4-Ki, 8-Ki, 12-Ki and 16-Ki crossings.
There were also long `ValueKey` traversals outside an active instance nesting
chain. The supervisor observed 0.945 seconds; the host stopped intentionally,
not successfully after a complete compilation.

The important causal distinction is that key expansion is already present while
the executable arena is small. This sample does not establish that table
cardinality, resource aliases, or specialization recursion cannot grow later.

## Isolated graph-key change

Only the lead's `AppendValueKey`, new `AppendScopeKey`, and `KeyProvenance`
definition/backreference changes were copied into the existing mirror. All other
mirror sources, the input and frozen dependencies were retained. The shallow
scalar-key fast path was unchanged. Known Scope regions, indexed resources and
captured locations receive first definitions and subsequent backreferences;
negative-region structural Scope values receive separate reference ordinals,
not a shared fictitious `-1` identity.

The pre-key arena state at entry 19 matched the original run exactly:

| Measurement | Original mirror | Graph-key mirror |
|---|---:|---:|
| Entry 19 key | unfinished at 16,411 | complete at 1,868 |
| Entry 19 functions / types / layouts | 9 / 1154 / 30 | 9 / 1154 / 30 |
| Entry 20 query key | not reached | 1,868, same hash as entry 19 |
| Largest completed key through entry 40 | not reached | 2,485 |

The patched run stopped at the 40-entry guard in 1.123 seconds. It had 9
functions, 49 templates, 1168 types, 35 layouts and 32 Scope constructions.
There were no 4-Ki key crossings. This is not a whole-program timing comparison:
the two runs stop at different points. It is a controlled demonstration that
graph definition/backreference encoding removes the observed early repeated
expansion before instance construction.

## Mechanism and next question

The earlier encoder gives regions canonical ordinal labels, but still expands
their bodies repeatedly. Its `active` set guards value-reference cycles on the
current recursion path and removes each value on return. That prevents a direct
cycle; it does not encode shared DAG nodes once. Callable capture graphs and
the separate `Element` / indexed-resource element paths can therefore revisit
the same semantic subgraph along many paths.

The key change addresses that representation cost. It does not by itself prove
that the underlying abstract resource domain is finite. The next controlled
question is whether newly generated resource views, carrier schemas or recursive
summaries continue to create distinct abstract states after keys are bounded.
Allocation-site abstraction and alias partitions must preserve separate
instances and write constraints; equal carrier types alone do not establish
object identity. A full insert is not claimed complete by these stopped runs.

## Progressive 120-entry run: the next growth point

The same patched mirror and exact input were then allowed 120 key entries,
retaining the 16-Ki key, 20,000-append, 20-second and managed-heap guards. It
stopped at append visit 20,001 after entry 108, not at the event-count limit.
The unfinished key at that stop was only 628 code units. Wall time was 1.600
seconds and Linux child peak RSS was 88,388 KiB. No 300-entry or unguarded run
was needed to locate the next mechanism.

Repeated query instantiations of template 14 (`BTree::insert_nonfull`) had the
same argument type IDs `(1152, 4, 4, 5)` and capture type IDs `(1054, 1153)`.
Every row below was a cache miss:

| Entry | Nesting depth | Caller objects | Indexed resources | Key length |
|---|---:|---:|---:|---:|
| 45 | 3 | 23 | 17 | 2454 |
| 60 | 4 | 37 | 32 | 2518 |
| 75 | 5 | 72 | 74 | 2692 |
| 90 | 6 | 144 | 155 | 3085 |
| 105 | 7 | 288 | 317 | 3917 |

Throughout entries 60–108 the type/layout/code counts stayed at 1169 types,
35 layouts, 9 functions and 32 Scope constructions. Entry 107, the nested
`lower` query, had a node argument with 127 Scope aliases, and a caller with
321 objects and 356 indexed resources. This distinguishes continuing abstract
resource/view growth from growth in the code or ordinary carrier schemas.

Graph key memoization removes repeated serialization of an existing graph.
The second observation identifies a separate non-converging specialization
state: successive recursive queries create larger resource/view alias graphs
despite the same exposed argument/capture types. The next change therefore
belongs in finite resource provenance and summary projection, not a higher key
limit. A successful complete insertion remains the acceptance criterion.

## Finite-role candidate: SCC hit and complete analysis

The next candidate refreshed the complete Compiler source mirror with the
finite resource/view-role implementation and shared `CodeSchema` normalization
before non-scalar instance-key encoding. HIR and Optimization stayed on their
frozen9 DLLs. Core was copied into a separate private mirror project because the
candidate uses the new `NativeBehavior.PreservesIdentity` member; neither the
product Core project nor the CLI was built. Four source-owner-supplied explicit
`SelectMany` lambdas corrected method-group/ImmutableArray compilation errors in
the copied projection file. The resulting mirror build had zero warnings/errors.

This is a combined candidate, not a claim that a single-line change caused its
whole effect. Its first 120-entry run already showed the required recursive
cache behavior:

| insert_nonfull query | Depth | Caller objects | Indexed resources | Key |
|---|---:|---:|---:|---|
| Entry 60 | 4 | 18 | 8 | 2339, `FC542F6B79C4` |
| Entry 66 | 5 | 17 | 8 | 2339, `FC542F6B79C4` |

Entry 66 returned without a `MISS`: it reused the reserved recursive instance.
The earlier doubling of objects/resources did not recur. Analysis progressed
to publishing executable functions rather than staying in signature queries.

At 300 permitted entries the old **global** 20,000-append guard stopped an
otherwise bounded-looking run at entry 136, with a key prefix of just 26 code
units. That guard was a cumulative experimental work limit, not a per-key cost
or another resource-growth observation. It was therefore raised once to
100,000, keeping the 300-entry, 16-Ki per-key, 20-second and managed-heap guards.
Per-key append deltas were added to distinguish them from global visits.

The unchanged one-insert input then completed `Compiler.Analyze`:

| Complete guarded candidate | Observation |
|---|---:|
| Instance-key entries | 159: 43 publish, 116 query |
| Cache misses | 84 |
| Maximum caller objects / indexed resources | 19 / 11 |
| Maximum complete key / per-key append visits | 3214 / 137 |
| Finalized functions | 31 |
| Host Analyze elapsed | 1404.451 ms |
| Supervisor wall time | 1.460 s |
| Linux child peak RSS | 74,184 KiB |

No experimental limit was reached. The actual publish recursion also reused
code: entries 117 and 159 had the same 1905-unit key, hash `AA6605697C58`, and
entry 159 had no miss. Its key took 98 append visits. These timings include
managed cold-start/JIT and tracing, not a Native AOT performance benchmark.
Completion here means source-to-finalized-HIR analysis only; BTree runtime
behavior and larger workloads belong to subsequent acceptance runs.

The source supervisor now requires a matching successful-build fingerprint
before running the host. An incidental stale-output launch after the initial
failed mirror build was quarantined and excluded; it is not candidate evidence.

## Exact three-line Compile input: callable-domain fan-out

A later acceptance input was **not** the two-line Analyze fixture above. It
includes the observable size/conversion/transfer tail and uses default Compile:

```msync
let tree = $std::containers::BTree::new(0, "");
$std::containers::BTree::insert(tree, 0, "item-0");
($std::containers::BTree::size(tree) as String) -> ./result.txt;
```

Its exact bytes, including line endings, have SHA256
`1c02eee6f28d1454c1f72e10e5b8dc56b0a10b8da42816ffb442d8e821e44fcb`.
The source was copied from `.temp/hir-cost-functional/btree-1/input.msync`;
the host reads that preserved input rather than reconstructing a similar string.
The Compiler mirror was refreshed from clean source HEAD
`781ca56ee8d268788ae3cf70e95dbfc290d512df`, with frozen13 Core/HIR/Optimization
DLL references. Mirror-only stage hooks surround Analyze, Lower and Optimizer
inside default `Compiler.Compile(..., 2)`. No runtime program was executed.

At both 120 and 300 instance-key entry limits, only `Analyze.enter` appeared.
Neither Lower nor Optimizer had started. The 300-entry run stopped in about
1.73 seconds with approximately 85 MiB child RSS, while functions/templates/
types/layouts/Scope-constructions stayed at `9/49/1186/36/32`. The tail was not
growing keys: template 31 (`Buffer::empty`) repeatedly hit the same 157-unit
key at the same nesting depth, caller object/resource counts `14/5`.

A third-visit FlowSolver probe found a real, ordinary refinement for logical
place 479: unassigned Any without an element became unassigned Any with
`Range<Number>` and semantic type ID 1185. At that point `Join(x,x)=x` and
replaying transfer on the same input were both stable. Raising that diagnostic
to a changing sixteenth visit did not trigger it in the repeating 300-entry
trace. A full stack sample showed actual `Scope` initializer analysis and
`Invoke`'s callable-choice enumeration, not FlowSolver, at the repetition.

The decisive probe stopped at a single call site with **384 source callable
choices**, all template 31 and all without a separate RuntimeIdentity. The
entire set had:

- one distinct capture-mode/canonical-storage-owner vector;
- one distinct shallow capture value-summary vector;
- identical first captures: location 110, canonical owner 109, type 1143,
  Scope region 783; and location/owner 296, type 26.

These were 384 alternatives of the same represented Buffer helper environment,
each causing another cache hit from one Scope initializer. The source callable
record contains an ImmutableArray of captures; representation-level inequality
can therefore survive a normal `Distinct` even when the captured environment
is semantically the same. Native-name-only de-duplication does not address it.

The resulting correction belongs in the callable domain's captured-environment
identity and joins. De-duplicating by template alone would incorrectly discard
different closures; changing worklist limits or replacing inferred values with
Any would address neither cause. This new exact-input finding does not turn the
earlier two-line Analyze result into an acceptance result for default Compile.

The decisive bounded fan-out trace has SHA256
`8d9a0f2011a00c9a3486268223fa518430dfddc087c00924f36db219a27d6f5d`,
Compiler mirror SHA256
`a1041d6ceedb85ab5f7b0eedb7aaaf70620e5c4fb6650f0fbb7861390abeb8a2`.
It stopped intentionally in about 1.70 seconds at fan-out greater than eight;
no public CLI or 24-insert run was repeated to obtain this diagnosis.

### Captured-environment comparer differential

Only the new `SemanticCallableIdentity` and the two Join/Choices comparer
replacement hunks were then copied into the existing exact-input mirror.
Source captures are compared by template/runtime identity, capture mode and
location; a by-location capture's stored Value is a flow hint rather than a
second identity. By-value captures still compare their snapshot/schema. The
raw source binding IDs in the demonstrated 384-way set were already identical,
so this experiment does not require guessing equality between different cells.

At source offset 18484, all thirteen observed calls after the change reported
`raw=1 choices=1` instead of 384. The same exact three-line default Compile(O2)
then completed every stage under the existing guards:

| Stage boundary | Elapsed ms | Function count |
|---|---:|---:|
| Analyze exit | 1978.881 | 33 |
| Lower exit | 2030.764 | 82 |
| Optimizer exit | 2579.166 | 66 |
| Host complete | 2599.414 | 66 |

Supervisor wall time was 2.671 seconds, with Linux child peak RSS 93,228 KiB.
These are cold managed, instrumented values, not a Native AOT benchmark. No
event/key/append/time/heap guard fired. The program's transfer was compiled,
never executed, so this is a complete default compilation result rather than
evidence about `result.txt` contents.

The before/after comparison isolates callable-domain representation: keys were
already cache hits, but the duplicate captured environments kept asking for the
same instance and repeatedly joining/projecting its effects. Removing that
fan-out allowed the frontend to finish and both subsequent stages to run.

The successful exact Compile trace SHA256 is
`91e3352a3c4e7816fe67e12864075fc41471f52a24c93bc1b4e33580361a8ffa`,
Compiler mirror SHA256
`48812c5c59ae8ce8bbeb041fc6f14f6c7e2839cce3536f154fd12bec183065bc`.
`exact13-fanout-before/` preserves the uncorrected trace/receipt/source Types
file, and `callable-patch-receipt.json` records precisely the two patched source
files and the mirror-only witness hook.

## Receipts and reproduction

The workspace retains:

- `source-receipt.json`: original source/dependency hashes;
- `baseline/`: original instrumented Instantiation source, trace helper, trace
  and run receipt;
- `key-patch-receipt.json`: the exact product-tail hash and old/new mirror hashes;
- `trace.log` and `run-receipt.json`: the graph-key run;
- `graph40/`: the earlier 40-entry graph-key trace and its original receipt;
- `graph120/`: the old memo-only candidate's full Compiler mirror and bounded
  resource-growth trace;
- `roles120/` and `roles300/`: the finite-role event/append-limited traces;
- `successful-build.json`, `summary.json`, and `snapshot-manifest.json`: the
  exact successful source fingerprint, aggregate counts and complete file hashes;
- the complete source mirror and separately built host.

The original and graph-key Compiler DLL hashes were respectively
`a12dcb545573c205b0a6c52386aa9a708eeb2b985620c735ae443ccea4a473c9` and
`c188f4e8ca4061082b092021a12fa1586d68ba2575170dde79f177fd9f725a16`.
The original trace SHA256 was
`936630f463676649e024a20f18f5783dc8d3357591ca8e70118fbccaf7622083`;
the graph-key trace SHA256 was
`e432545ea90e9afcb9bd8df8ad8a6d27663f8d511823320502633ad9e57b675d`.
The progressive 120-entry trace SHA256 was
`378d5f762953b7e9728bb2f0e0bc713295cc27d783a1077a86713c0fbf3f6c43`,
with Compiler DLL
`9855606573ae05487f318b84d43e3a3903e84ea91a9fcdd7f98b9c37ca23b3c4`.
The instrumented Instantiation source remained
`6f7d2f0b529621855d143c52757f3c11352a1ebc957c91b8abb63f414b6a9cec`;
only the trace helper's event limit changed.
The complete finite-role candidate's trace SHA256 was
`a8e6d94aff3f53e6170e555686e8b2d75d392c96c74e8b590998f81a1ca2db4b`;
its Compiler DLL SHA256 was
`32caec857fc1488cebd3876765b65728c1d8f3628bf96058f48fba773184cb43`.
Its private Core DLL was
`830b0b93e537afc4574ae4e1ab8d7df684cf2d5faf8647fd46e9c6d8fa6eed59`.
The exact input bytes have SHA256
`7098218d249fdcb8d326f1803bc62734f080f03a51d8c0d4bf12fcdcd1491fa7`.

Build only the mirror host:

```text
python3 .temp/hir-finite-trace/build.py
python3 .temp/hir-finite-trace/run.py
```

To reconstruct the original variant in this preserved mirror, first restore its
Instantiation source and trace helper from `baseline/`. Managed assembly
informational versions include the repository revision at build time, so a
different surrounding Git HEAD can change the DLL hash without changing this
recorded source experiment. The source manifest and dependency hashes identify
the compared programs independently of that generated version attribute.
