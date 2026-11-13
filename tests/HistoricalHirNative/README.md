# Historical typed HIR, one native backend

HIR format 3 is an independently versioned, source-free input contract. An old
public Set/Heap program embeds its old typed standard-library bodies, including
historical native bindings that the current source library no longer exposes.
Such an artifact must lower to builtin layouts, addresses and ordinary memory
operations; it must not require the old source, a second VM or a live allocator
runtime kind.

```sh
python3 tests/HistoricalHirNative/journey.py \
  --frozen-cli PATH_TO_0_8_6_NATIVE_AOT_MSYNC \
  --native-cli PATH_TO_CURRENT_MSYNC
```

The typed infrastructure script obtains an independent result from the frozen
producer, exports HIR and deletes the source before any current compilation.
It preserves the exported bytes and also removes **only** optional diagnostic
source texts from a second HIR copy, leaving typed tables unchanged. Both inputs,
checked native objects and bound executables run at O0/O2. Native disassembly
must contain neither physical Storage types nor retired native allocator calls.
The frozen CLI is a fixture producer, never a product dependency.

The cases cover public source collections, primitive vector membership, snapshots
of shared Scope references, destructive extraction, first-class native bindings,
slot reuse, arena links and logical Type metadata. Old compile-time reflection
is checked against its actual frozen result rather than a newly invented dynamic
reflection policy. Exact per-command stdout/stderr and failed artifacts remain
under the uniquely named repository-root `.temp/historical-hir-native/` journey.

`NativeHistoricalBindings` reuses the shared native memory emitter to build a
validated fragment; no old stack program is generated or executed. Native HIR
lowering relocates that fragment's finite tables, maps historical Storage
contracts to ordinary address/layout ABIs, and links direct and first-class
bindings as native functions. Logical Type descriptions remain independent of
physical range/field storage. Current source and current HIR without historical
bindings do not enter this migration path.
