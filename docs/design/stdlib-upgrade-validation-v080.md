# Source-composed library upgrade: user-journey verification

## Contract and scope

`tests/Msync.EndToEnd/stdlib_upgrade_journey.py` exercises the new library as a
user program, not by directly calling implementation helpers. It accepts either
a Native AOT CLI or a managed CLI DLL. Every selected program is checked without
executing its effects, then executed at O0, O2 and O3 through three independent
routes:

- source execution;
- source-free relocatable `.mso` execution;
- source-free context-bound `.mexe` execution from a different launch directory.

Each route owns a separate fixture directory under `.temp/stdlib-upgrade` and
compares exact output bytes. Paths include a non-ASCII source filename. The
fixture programs are independent of implementation test expectations.

| Journey | Observable contract |
| --- | --- |
| Collection alias and snapshot | An alias mutates the same list; an earlier snapshot remains stable; another factory instance is independent; Range callbacks compose with a File service |
| Map and Set equality | Caller-supplied equality determines duplicate keys/items; insertion order is stable; replacement does not mutate earlier entry snapshots |
| Unicode Builder and Regex | Builder aliases share state but independent factories do not; indexes/lengths count Unicode scalars; named groups retain their value; clear starts a new sequence |
| Environment snapshot and narrowing | Set/get/unset observe actual process environment; an earlier variables Range is a snapshot; nullable values narrow before use |
| Files and Commands | Replacing and appending preserve exact UTF-8 bytes; directory enumeration sees the published entry; commands retain stdout, stderr and a nonzero exit code |

The command child uses the harness's explicit `sys.executable`, passed as an
ordinary configured executable with arguments. Python is test infrastructure,
not a product implementation dependency. The report records the interpreter
version. The library and its compiled artifacts run through the native product.

## Incremental use

```text
python3 tests/Msync.EndToEnd/stdlib_upgrade_journey.py \
  --binary PATH_TO_MSYNC --case unicode-builder-regex --level 2
```

Omit `--case` and `--level` for the complete new-feature journey set. A failure
prints the actual CLI command context and diagnostic output. Fixtures and the
JSON report remain available for reproducing the selected failure.

## Performance method

The same harness optionally alternates baseline/candidate real CLI checks for
no-library and Bool-only scripts, with two warmups and fifteen measured
invocations per image and workload. It records raw process-inclusive milliseconds,
executable SHA-256 and size, host identity and Python version. These common
workloads discriminate accidental loading of an enlarged library from the cost
of explicitly requesting a new module. They do not measure warm in-process
compiler allocation or claim that a managed CLI and Native AOT are equivalent.

The preserved previous accepted Native AOT image is
`.temp/stdlib-upgrade/previous/msync`, SHA-256
`2b2f40a00e3767fad638b30c096017de671ee1434a81984b9a8218e4587b2a4f`.
Its source freeze is the prior standard-library delivery `e85e6170598dfd38adefbcb778e43647686c3096`.
The initial standalone observation was made while multiple compiler builds were
active. Its large spread is not used as evidence for a performance conclusion;
a paired native-image measurement belongs after the candidate freezes.

## Acceptance evidence

Implementation and integrated journey evidence will be appended once the
corresponding source/compiler/native contracts are executable. New source being
present is not itself acceptance of its closures, callbacks, effects or binary
round trips.
