# Independent HotSpot mechanism review for the msync interpreter

## Decision

The November 9, 2026 review supports testing resolution predecoding and narrowly
guarded interpreter fast paths. It does **not** establish that a JIT, a PIC, or
an OSR subsystem is necessary. No production code or new timing measurement is
part of this note. The parent [runtime research note](managed-runtime-optimization-v080.md)
owns the combined recommendation and experimental results.

The reference version is JDK 27 GA: Oracle records its release on September 15,
2026 in the [official release notice](https://docs.oracle.com/en-us/iaas/releasenotes/java-management/jdk-27-release-note.htm).
Implementation references below use the `jdk-27-ga` source tag rather than the
moving development branch.

## Corrections that affect the experiments

1. **Quickening is not the compiled inline-cache state machine.**
   HotSpot's [Rewriter::compute_index_maps / rewrite_method_reference](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/interpreter/rewriter.cpp)
   resolves constant-pool addressing into runtime cache entries. Its
   `maybe_rewrite_ldc` chooses fast reference-loading forms. These operations
   are not all triggered by a hotness threshold. Conversely,
   [CompiledIC](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/code/compiledIC.hpp)
   is compiled-call machinery with clean, monomorphic and megamorphic states.
   A small interpreter PIC would be an msync policy, not a literal copy of this
   state machine. The original [SELF PIC research](https://bibliography.selflanguage.org/pics.html)
   is the closer model for retaining several receiver cases.

2. **A resolved instruction can still require work on every execution.**
   [TemplateTable::patch_bytecode](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/cpu/x86/templateTable_x86.cpp#L151)
   deliberately leaves a putfield instruction unquickened when the resolved
   entry's put-code is zero: the runtime must perform additional work on every
   execution. The same file's bytecode patching handles debugger breakpoints
   instead of replacing the instruction stream blindly. This is an important
   constraint on fusion, not merely an implementation detail to omit.
   [The bytecode table](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/interpreter/bytecodes.cpp#L28)
   records fast typed field forms and combined access/load forms, retaining
   their ordinary Java-bytecode identity. For msync, a fused plan must retain
   original PC/span attribution and required Cell reads, cancellation checks,
   callback execution and work-owner boundaries.

3. **Tier selection is an economic policy, not a universal invocation count.**
   [CompilationPolicy](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/compiler/compilationPolicy.hpp#L97)
   documents interpreter/C1/C2 levels, invocation and backedge feedback, queue
   load, event rates, stale requests and trivial-method treatment. These facts
   support measuring expected remaining work and preparation cost. They do not
   justify copying JVM numerical thresholds into short msync commands.

4. **Local miss fallback is much smaller than arbitrary deoptimization.**
   [Deoptimization::rematerialize_objects](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/runtime/deoptimization.cpp#L263)
   participates in restoring eliminated objects and frame state. An msync IC
   that guards before changing the interpreter stack and executes the original
   operation on a miss does not need that machinery. Speculatively deleting
   Scope identity or stores and trying to recover later would need materially
   more state correspondence, including captured-cell aliases and task owners.

5. **Leyden AOT caches are not Native AOT executables.**
   [JDK 25's shipped method profiling](https://docs.oracle.com/en/java/javase/25/migrate/significant-changes-jdk-25.html)
   makes previous-run profiles available to the JVM's JIT. The
   [JDK 26 object-cache change](https://docs.oracle.com/en/java/javase/26/migrate/significant-changes-jdk-26-release.html)
   loads objects from a GC-neutral representation. Neither implies that an
   existing .NET Native AOT interpreter can enable CLR tiering with an
   environment variable or serialize live Scope/Cell instances as a profile.

## Local code correspondence

`Interpreter.Specialize` currently selects a code-table target while copying a
`FunctionValue`. Removing that temporary allocation is valid only if invocation
continues to use the **actual** closure's captures and native identity; the
cacheable fact is a target selection, not a previously observed closure object.

`ScopeValue.GetField` reads through a possible `CellValue`; a layout/offset hit
must call the equivalent live read rather than retain the previous payload.
`ScopeValue.IsField` already protects the compiled slot hint by authoritative
member name. A layout IC needs comparison against that existing path, not only
against name lookup with hints artificially disabled.

`Interpreter.RefreshSessionImage` replaces projected code and block tables and
clears dispatch data when its session generation changes. New caches need the
same lifetime boundary. The projection belongs to the deferred interpreter's
retained binding context: code from a new generation cannot simply be mixed
with an old or new unrelated constant table.

## Three practical falsifiers

| Discriminator | Required observation | Incorrect optimization exposed |
| --- | --- | --- |
| Same code, different closure instances | Two factory results using one function ID return distinct captured values; later mutation is visible through the appropriate original Cell | Caching a FunctionValue/capture array as if code identity implied closure identity |
| Same field operation, changed objects and payloads | Alternate layouts with different field order, then mutate a shared captured field without changing the layout; every read returns the selected object's current value | Scope-type-only guards, offset reuse without shape protection, or cached field payloads |
| New session generation with old deferred origin | An old range created in A observes a later callable assignment, while uncaptured literals retain caller A and an explicitly captured Path from B retains B | A cache keyed only by numeric function ID, or code/constant/context projections from different generations |

Run these against retained O2 sites. A source expression already removed by
static SRA, IPA or CTFE cannot validate the dynamic cache path. Compare cold
preparation and prepared execution separately; highly variable receiver sites
must continue through normal resolution rather than fail a cache-capacity rule.
