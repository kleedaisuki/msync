# Private Number locals acceptance

Run from the repository root with the product tree and a built CLI:

```sh
dotnet run --project tests/TypedLocalFrames -c Release -p:RestoreLockedMode=true
MSYNC_JIT=off dotnet run --project tests/TypedLocalFrames -c Release --no-build
python3 tests/TypedLocalFrames/journey.py path/to/msync
MSYNC_JIT=off python3 tests/TypedLocalFrames/journey.py path/to/msync
```

The host journey round-trips format-6 objects and bound images through the actual
reader. It checks exact negative-zero and quiet-NaN sign/payload bits at generic
return, storage materialization, capture promotion and numeric region exit. The
mixed activation updates a shared Cell while other locals stay raw. It then
calls a split borrowed frame with two raw Numbers and an ordinary Scope. The
callee detaches, rebinds the Scope argument, and suspends on gated input. Its
child process remains reachable through the original Scope: only the caller can
deliver stdin and observe the echo. A private slot overwritten by language
`null` must not expose its old Number.

The hot loop verifies both exit parities and that changed local values were
committed without boxing; the zero-trip loop and execution session exercise
cold and persistent storage respectively. On Linux AArch64 the host may lack
the instruction-cache publication prerequisites, so the host journey accepts
the managed numeric tier there as well. On Linux/Windows x64 it checks actual
native entry when JIT is enabled.

`--scope-storage-only` selects the format-6 typed operand transport checks. An
early field takes a snapshot of the numeric bits before subsequent slot
rebinds and a gated input read, but a Cell field continues to alias its
original storage. The fixtures preserve signed zero and NaN payloads
through a duplicated operand at a branch join and separate loop rows.
A storage value that could instead return directly stays on the generic
lane even on the branch that constructs a Scope. Logical LoadStorage PCs
and visit counts stay intact; `raw_scope_storage_loads` counts only the
selected raw Number loads, not Cell references or declined sites.
With three consecutive storage loads and a two-field constructor, the
first operand survives for generic return. Two runs of the same image
give that operand either a raw Number or Null; neither changes the two
raw loads consumed by the constructor.

The CLI journey uses the ordinary O2 compiler, an input-dependent loop and a
mutable captured binding. It removes the source before executing its `.mso`,
and the object before executing its bound `.mexe`. It checks that stdout is
unchanged with telemetry disabled, then reads `raw_local_writes` and
`numeric_local_commits` from the opted-in run. Both journeys accept
`--semantic-only` for pre-lane binaries.

The compact-suffix checks exercise a borrowed raw prefix below the first
writable slot, both generic materialization at a nonzero offset and a direct
Scope operand preserving exact binary64 bits. They also shift the native
exit fixture's locals by three logical slots. Opt-in
`number_sidecar_arrays`/`number_sidecar_slots` verify physical compaction
independently of the logical results.
