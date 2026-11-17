# Rejected unpooled Native format 1 fixture

`unpooled-v1-prereflection.mso` is the original 29 KiB record-stream artifact from
the frozen early Native AOT image
`6771d578cadb7d8de1df40953c7fb28b7bfbd1ce946c463cb0a25a235d65c9e2`.
Its function records precede the optional `ReflectionSignature` extension. The former reader detected that unversioned layout. Current readers do not probe
or import it; storage plans are mandatory in the current format. The CLI journey
uses these frozen bytes to check that the loader asks for recompilation.
