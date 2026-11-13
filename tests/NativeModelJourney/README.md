# Native-model real CLI acceptance

`native_model_journey.py` exercises the v0.9 storage model through the real CLI,
not a second interpreter or an in-process mock. The default requires native IR
evidence; `--semantic-only` explicitly establishes a migration baseline without
claiming that the new representation is accepted.

```sh
python3 tests/Msync.EndToEnd/native_model_journey.py \
  --binary .temp/v090/cli/msync --work .temp/v090/native-model-acceptance
```

The default is O0/O2/O3 and all eight cases. `--case` and `--level` select a
changed contract. Work must be a new/empty child of repository-root `.temp`;
`result.json` checkpoints active work and all prior accepted routes. Full stdout,
stderr, commands, input hashes and failure diagnostics remain in its `logs/`.

## What is checked

* Null, Bool, Number, String, Path, Connection, Function, Range and Type pass
  through source calls, returns and captured locations into mixed Scope fields.
  Process additionally survives a function return through a Scope and a captured
  read, then waits on real input. The child records its launch count, detecting
  selected-effect replay. Python is only that test child, not a product dependency.
* Fresh allocations, shared aliases, rebindings, field/shared-capture writes,
  empty namespaces, recursive references, recursion, finite unions and callee
  evaluation before a mutating argument have independent expected outputs.
* Lazy stdin consumes exactly the demanded prefix: parsing a discarded invalid
  tail would fail. Path publication compares complete binary bytes, including
  NUL/non-UTF8, after a bound executable is launched in an unrelated cwd.
* Merely retaining an unselected SSH Connection/qualified Path must not launch
  a client. POSIX runs put a recording/failing `ssh` guard first on PATH; every
  host also receives a deliberately nonexistent SSH config. The receipt records
  whether the strong process guard is available rather than claiming it on Windows.

Every case runs from source, standalone `.mhir`, detached `.mso`, and detached
`.mexe`. Physical source/intermediate files are removed; source-free HIR is also
lowered and inspected. Bind/compile must not execute output effects. The `.mexe`
route removes its portable input and executes elsewhere, checking its captured
binding context. Different valid field layouts need not produce identical bytes:
hashes and byte identity are recorded, while semantic outcomes are the contract.

## Structural evidence and current gap

The Linux x86-64 Native AOT integration image built on 2026-11-12 passes all
eight cases at O0/O2/O3 through all four routes, with native structural evidence,
deleted-source inputs and no unwanted SSH process. Image SHA-256:
`6771d578cadb7d8de1df40953c7fb28b7bfbd1ce946c463cb0a25a235d65c9e2`.
Local receipts are `.temp/native-model-aot1-20261112/result.json` and
`.temp/native-model-aot1-o3-20261112/result.json`; the fixed
image is `artifacts/validation/native-v090-integration-aot1/msync`. This is
integration acceptance for those journeys, not final retirement, platform,
collection coverage or an end-to-end performance result.

Disassembly must identify `representation: "native-v1"` and contain actual
instruction evidence. Old generic Scope/Cell/Storage operations and legacy
arena/vector/slot native APIs are rejected. Both direct and standalone-HIR
lowering and the bound executable are checked. This gate is additional to, not a substitute for, auditing
that the compiler/VM actually uses the native representation and typed lifetimes.

The frozen 0.8.6 baseline passes seven cases at O0 across all four routes. Its
two-node cyclic traversal is a real failure: after printing `1,2`, the old
layout admission rejects the next recursive link. That case is retained rather
than weakened. Baseline receipts are under `.temp/native-model-baseline-o0-v3`,
`...-v4`, and `...-rest`; the failed invocation retains its complete source and
diagnostic. No baseline result is v0.9 architectural acceptance.

Focused validator checks can be run with:

```sh
python3 tests/NativeModelJourney/test_journey.py
```
