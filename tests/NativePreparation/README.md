# Native contextual preparation

Build the CLI, then run `python3 tests/NativePreparation/test_journey.py` (or pass
`--binary` for a published Native AOT executable). Each journey compiles `.mso`,
binds `.mexe`, inspects its native plans, removes source/object inputs and runs
from another working directory. O0 and O2 cover dead failed transfers, frozen
metadata/control and regex membership, opaque writes, publication dependencies,
known source wrappers, preservation of captured mutation, demanded Bool results,
iterator membership, directory-merge survivors, and physical symlink aliases. Changed filesystem
state after binding distinguishes recorded observations from runtime replay.

`dotnet run --project tests/NativePreparation -c Release` also exercises an
accepted native IR program whose transfer result already has an address alias.
The observed failed Bool must survive dead-write analysis, binary round-trip and
execution; actual failure accounting and concrete diagnostics are checked. The
same journey checks cancellation and strict executable no-replanning after
artifact decode, including empty-plan encoding.

The semantic equivalence property covers 126 explicit/implicit scalar conversion
cases through contextual preparation and binary round-trip, comparing exact
values, failures and source spans with direct execution. Two callable metadata
controls distinguish public reflection signatures from physical pointer ABIs.
These guard shared semantics, not a second evaluator or runtime validation layer.

Native AOT contract acceptance:

```console
dotnet publish tests/NativePreparation -c Release -r linux-x64 --self-contained -p:PublishAot=true -o artifacts/native-preparation-contracts/linux-x64
artifacts/native-preparation-contracts/linux-x64/Msync.NativePreparation.Tests
```
