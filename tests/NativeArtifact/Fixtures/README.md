# Unpooled native-v1 fixture

`unpooled-v1.mexe` is the exact 948-byte encoding of `Fixtures.Plan()` produced
by the pre-pooling v0.9 development codec on 2026-11-12. It has native IR format
1, ordinary independent plans, captured paths and an unpaired UTF-16 surrogate.
The compact reader must preserve this already accepted representation without
source, an old VM, or a second execution model. The test compares the decoded
document against the current ordinary typed fixture.

Do not regenerate this file with the current writer: its purpose is compatibility
with the earlier encoding, not a golden copy of the newest bytes.

`unpooled-v1-prereflection.mso` is the original 29 KiB record-stream artifact from
the frozen early Native AOT image
`6771d578cadb7d8de1df40953c7fb28b7bfbd1ce946c463cb0a25a235d65c9e2`.
Its function records precede the optional `ReflectionSignature` extension. The
reader recognizes that earlier unpooled layout as well; absence means no logical
signature override. The compact encoding adds an explicit discriminator/revision
instead of repeating that unversioned-extension ambiguity.
