# Native artifact contracts

`dotnet run --project tests/NativeArtifact -c Release` checks the real codec and
strict native IR validator, including all builtin literals, exact Number bits,
captured paths, closures, typed memory/control, logical Range descriptors,
portable plans, streamed reads and malformed input. `CompactEncodingChecks`
checks exact UTF-16 pooling, one-byte Nop code, sparse source mappings, optional
operands, deterministic re-encoding, bounded sections and invalid indices/varints.
Versioned fixtures retain both accepted unpooled native-v1 function layouts; do
not regenerate them with the current writer.

Detached codec/inspection acceptance:

```sh
dotnet run --project tests/NativeArtifact -c Release -- produce .temp/codec-plan.mexe
dotnet run --project tests/NativeArtifact -c Release -- consume .temp/codec-plan.mexe
```

For a controlled encoding/loading comparison, `measure OLD_INPUT OUTPUT.json`
reads an original unpooled native-v1 artifact and writes its compact encoding,
checks complete rendered-document identity, then alternates seven warmed parse +
validation + allocation-hint-normalization trials. It records bytes, SHA-256,
allocation and elapsed samples, and writes the compact artifact next to the
report. Timing excludes source compilation and guest execution; it is not an
end-to-end VM throughput result. Both frozen unpooled fixtures remain accepted;
the retired production encoder is not retained solely to reconstruct benchmark
inputs. Supply an original unpooled artifact, such as the fixture under `Fixtures/`.

Use `--artifacts-path artifacts/NAME` for isolated managed builds, or publish the
same project with `-p:PublishAot=true -r linux-x64` for a Native AOT codec probe.

`python3 tests/NativeArtifact/test_journey.py --binary PATH` re-encodes an
unchanged earlier native-v1 input through the real CLI, removes the portable
input/source, inspects and executes compact output, then binds it and executes
the `.mexe` from another directory against the independent record-stream oracle.

The 2026-11-12 controlled encoding receipt reduced the record stream from
29,197 to 6,088 bytes and a real Set artifact from 4,091,313 to 653,033 bytes,
without dropping their full rendered document or source mappings. Validation
dominates allocations: managed Set loading changed about 139.2 to 136.7 MB,
not an 84% allocation reduction. Local reports are under
`.temp/compact-native-ir/{record-flat-debug,set,record-aot}.json`; they are
diagnostic load windows, not guest execution or whole-product speed claims.
