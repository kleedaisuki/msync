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

### First integrated effect boundary

Producer managed snapshot 2 (`.temp/language-upgrade-managed/snapshot-2/msync`)
accepted the environment and file/command journeys at O2 through all three
routes, including source-free bound execution from another working directory.
There were six successful executions and thirty exact output comparisons.
`.temp/stdlib-upgrade/snapshot2-env-services.json` retains the observations.
An earlier image demonstrably returned a later environment value from the
supposed snapshot; eager native Range results corrected that observable defect.

### Actual SSH service composition

The optional Linux loopback fixture is reused from
`legacy/benches/ssh_v065.py` as development infrastructure. No Rust or Python
code participates in product execution. Select just this journey with:

```text
python3 tests/Msync.EndToEnd/stdlib_upgrade_journey.py \
  --binary PATH_TO_MSYNC --ssh-fixture --case ssh-file-and-command
```

It exercises `files::File(remote_path @ endpoint)` writing, appending, reading
and copying to local storage, and `commands::Command(...)::on(endpoint)`
capturing actual remote stdout/stderr and status 9. A filename contains spaces
and Unicode. Checking, compilation and binding must launch no SSH client.
Every selected route must use exactly five owned SSH channels (four file
operations and one controlled process), with no `scp` subprocess.

Snapshot 2 accepted all three O2 source/`.mso`/`.mexe` routes, fifteen output-byte
comparisons, zero compile/bind authentications and five SSH clients per execution.
The fixture and exact component SHA-256 identities are recorded in
`.temp/stdlib-upgrade/snapshot2-ssh.json`.
