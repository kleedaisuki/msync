# Native artifact contracts

The v0.9 release reads Native format 2 (compact revision 2) and HIR format 4.
Old artifacts must be rebuilt from source. Readers decode the compiler's storage
and call plans; they do not repeat semantic verification, alias/escape analysis,
or allocation-hint normalization. The explicit `NativeArtifact.Validate` API is
still available for compiler/test diagnostics.

Current real CLI acceptance:

```sh
python3 tests/NativeArtifact/test_journey.py --binary PATH_TO_MSYNC
python3 tests/CallLowering/storage_journey.py --binary PATH_TO_MSYNC --work .temp/storage-journey
python3 tests/Telemetry/compiler.py PATH_TO_MSYNC
```

The artifact journey rejects a frozen old-native input, compiles current source,
removes source, inspects/executes its object, binds it, removes the object and runs
the bound executable from another directory. The storage journey covers source-free
HIR lowering, O0/O2, Off/Auto/Force, selected transfer lifetime and old Native/HIR
format rejection. Telemetry verifies that decoding does not invent compiler
analysis spans and that compilation reports storage/call decisions.

Hand-constructed Native IR must be finalized with `NativeOptimizer.PlanStorage`
(or `NativeOptimizer.Optimize`) before encoding/execution. The source compiler
already produces these plans. See the
[compiled storage contract](../../docs/design/native-execution-lowering-v090.md#v09-compiled-storage-contract-2026-11-13).

## Historical codec diagnostics

The older C# codec fixture/measurement harness describes the pre-contract codec:
unpooled-v1 acceptance, malformed semantic inputs, and parse + validation + hint
normalization timings. Those acceptance expectations are superseded by the v0.9
contract above, not runtime compatibility requirements. Frozen files in `Fixtures/`
remain useful as rejection inputs and must not be regenerated with the new writer.

The 2026-11-12 historical receipt reduced a record stream from 29,197 to 6,088
bytes and a Set artifact from 4,091,313 to 653,033 bytes. Those are historical
encoding receipts, not measurements of the current execution contract.
