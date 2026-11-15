# Rejected unpooled native-v1 fixtures

The v0.9 release rejects these files. They are preserved to exercise the explicit
rebuild boundary, not as inputs to a compatibility importer.

`unpooled-v1.mexe` is the exact 948-byte encoding of `Fixtures.Plan()` produced
by the pre-pooling v0.9 development codec on 2026-11-12. It has native IR format
1, ordinary independent plans, captured paths and an unpaired UTF-16 surrogate.
The former compact reader accepted this representation. That compatibility path
has been removed; the current CLI rejection journey uses these frozen bytes.

Do not regenerate this file with the current writer: its purpose is testing rejection of
the earlier encoding, not serving as a golden copy of the newest bytes.

`unpooled-v1-prereflection.mso` is the original 29 KiB record-stream artifact from
the frozen early Native AOT image
`6771d578cadb7d8de1df40953c7fb28b7bfbd1ce946c463cb0a25a235d65c9e2`.
Its function records precede the optional `ReflectionSignature` extension. The former reader detected that unversioned layout. Current readers do not probe
or import it; storage plans are mandatory in the new explicitly versioned format.
