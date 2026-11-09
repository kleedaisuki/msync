# Typed HIR binary artifact codec

This executable constructs semantic HIR arenas directly and calls the production
codec. It does not serialize compiler implementation objects, reconstruct syntax
or embed a bytecode image.

The mixed fixture exercises every model table, including generic bodies,
associated types and concept requirements, both capture modes, template/function
dispatch identities, range demand modes, callable adaptations, source identities
and exports. Canonical byte roundtrips distinguish omitted fields from a merely
similar printed result. Number payload checks retain negative zero and distinct
NaN bits; Unicode, raw backslashes and literal LF/CRLF remain exact.

Malformed cases cover every prefix of a valid scalar artifact, magic/container
and semantic versions, impossible/negative physical counts, invalid UTF-8,
boolean/node/effect tags and invalid semantic references. The count checks derive
from bytes actually remaining in a seekable input, not a configured node quota.
Non-seeking input is supported without eagerly allocating the claimed capacity.

The file workflow checks owned atomic replacement and preservation of the old
artifact when a new semantic module is invalid. Streams remain owned by callers.
Loaded scalar modules lower with the production backend while their optional
diagnostic text is deliberately not valid source. Repeated lowering produces
identical bytecode and retains the original floating-point payload bits.

```sh
dotnet run --project tests/HirArtifactCodec \
  -c Release --artifacts-path .temp/hir-artifacts/codec-build
```

The source-level export/delete/lower/execute journey belongs to the HIR artifact
integration lane; these manually built modules keep codec failures independent
of the frontend being migrated.
