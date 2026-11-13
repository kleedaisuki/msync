# Native builtin bindings (0.9 migration)

```sh
dotnet run --project tests/NativeBuiltinsV090 -c Release
```

This executable exercises the native host binding ABI directly. It does not
certify that the CLI/compiler has completed its migration. Source, detached
artifact and persistent-session acceptance remain the end-to-end gate.

The checked journey includes all math and Word32 declarations; Unicode scalar
text operations; Path/String/Connection conversion without authentication;
lazy callbacks, zero demand, single producer evaluation, typed materialization,
stable sorting and sorted combinations; metadata; regex records; formatting;
immutable text chunks; actual environment and process enumeration; atomic file
publication through injected streams; and captured working directories across
a later session `cd`. Temporary inputs and outputs stay under repository-root
`.temp/native-builtins-v090`.

## Binding and ownership boundaries

`NativeBuiltins.Resolve(symbol)` returns an immutable `NativeBuiltinOperation`
with a typed handler and declaration. An executable image resolves a static
native call once and uses `InvokeProducedAsync`; dynamic/public invocation can
use the checked `InvokeAsync` overload. The resolution cache contains symbols
and declarations, never caller values, directories, captures or live handles.

Bindings use only the closed `NativeValue` sum and concrete native descriptors,
not the retired Value inheritance tree. Host-returned records use explicit
`NativeLayout` declarations over builtin fields; they never infer layouts from
the current data. Ordinary language member access is not implemented through
the reflection helper. Dynamic member selection exists only for requested
metadata/formatting operations.

`NativeRuntimeContext` carries owned/injected streams and captured directory
state. Transfer and process hooks receive the actual context explicitly, so
copying a hook does not close over a directory that later mutates. Function,
field and value description hooks receive static interpreter metadata rather
than a generic language object.

`NativeRangeDemand.Defer` memoizes a transparent source-function producer;
caller-provided roots describe retained native values without enumerating the
Range. Adaptors retain source/callback roots; eager materialization publishes
a new typed snapshot and does not retain discarded source membership. A zero
take has no source demand. Native call result ABI metadata must refine Range
element types through `WithItemType`, rather than discover them by sampling
runtime elements.

The equality used by collection algorithms preserves language-library numeric
behavior (equal zero signs and equal NaNs), separately from exact-bit native
representation equality. Logical namespace references retain identity and
shared fields through snapshots. No container-specific allocator or native
Storage kind is introduced.
