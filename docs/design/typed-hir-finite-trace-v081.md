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

## Receipts and reproduction

The workspace retains:

- `source-receipt.json`: original source/dependency hashes;
- `baseline/`: original instrumented Instantiation source, trace helper, trace
  and run receipt;
- `key-patch-receipt.json`: the exact product-tail hash and old/new mirror hashes;
- `trace.log` and `run-receipt.json`: the graph-key run;
- `graph40/`: the earlier 40-entry graph-key trace and its original receipt;
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

Build only the mirror host:

```text
dotnet build .temp/hir-finite-trace/TraceHost.csproj -c Release \
  --artifacts-path .temp/hir-finite-trace/build --no-restore
python3 .temp/hir-finite-trace/run.py
```

To reconstruct the original variant in this preserved mirror, first restore its
Instantiation source and trace helper from `baseline/`. Managed assembly
informational versions include the repository revision at build time, so a
different surrounding Git HEAD can change the DLL hash without changing this
recorded source experiment. The source manifest and dependency hashes identify
the compared programs independently of that generated version attribute.
