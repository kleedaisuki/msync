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
| Collection alias and snapshot | An alias mutates the same list; an earlier snapshot remains stable; another constructed instance is independent; Range callbacks compose with a File service |
| Map and Set equality | Caller-supplied equality determines duplicate keys/items; insertion order is stable; replacement does not mutate earlier entry snapshots |
| Unicode Builder and Regex | Builder aliases share state but independent instances do not; indexes/lengths count Unicode scalars; named groups retain their value; clear starts a new sequence |
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

## Canonical Scope API migration

The unpublished 0.8 library now has one public interface: PascalCase named Scope
families and snake_case associated functions with explicit instance arguments.
The active journeys use `containers::{List,Map,EqualitySet,Stack,Queue}`,
`files::{File,Directory}`, `commands::{Command,Completed}`,
`text::StringBuilder` and `regex::Pattern`. They do not restore removed factory
functions or instance method closures. The File save example consumes the
supplied `concepts::TextFile` contract directly.

The `library`, `empty-collections` and optional `remote-library` packaging phases
remain active and use those same canonical declarations. Their programs were
migrated rather than dropping the real IO and transport observations. The
separate `collections_journey.py` now accepts `--level` for an incremental
optimization selection.

The migration used an immutable managed Release snapshot under
`.temp/canonical-source-migration/snapshot-1/`; its Compiler SHA-256 is
`ace1a853119d3f41ddbca0f505c5b7da78ab34707fcec7379cb3218a9ac690a7` and Runtime
SHA-256 is
`844f8148fff434c0a8999094f22024338a0d900d0232f2674373afa9152e7a0e`.
Only affected source/API boundaries were selected:

| Selection | Accepted observation |
| --- | --- |
| Empty families | Three O2 source/object/executable routes, 23 observations each, and three fixed-schema or comparator rejections |
| Collection mutation and aliases | Three O2 routes, 35 observations each, and three fixed-schema rejections |
| Unicode text/regex and File/Command composition | Six O2 routes, 45 exact output-byte comparisons; binary source removal and different executable launch directory retained |
| Actual SSH File/Command composition | Three O2 routes, 15 output-byte comparisons, five SSH clients per execution and no authentication during check/compile/bind |
| Standalone service fixture | `tests/StdlibServices`: 72 observations across 18 O0/O2/O3 staged executions, including actual child-process input/output and argument snapshots |

After formatting the seven inline source programs with the product formatter,
their actual CLI checks passed. The two migrated collection/library source
examples not covered by the selected service/text routes additionally produced
their nine expected output files. Python formatting, Ruff and mypy passed for
the three migrated journey drivers. These are managed semantic observations,
not a Native AOT performance or platform claim. Native delivery consumes the
same current scripts without repeating the earlier complete expansion matrix.

Reports are under `.temp/canonical-source-migration/`, including
`library-results.json`, `remote-results.json` and
`formatted-cases/report.json`; standalone fixture details are in its README.

## Earlier source-factory expansion acceptance

The following records exercise executable source/compiler/native contracts.
New source being present is not itself acceptance of its closures, callbacks,
effects or binary round trips.

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

### Structural concepts over actual library objects

The File/Command program additionally declares `WritableText` with a
`write: fn(value: String) -> Null` requirement and calls a generic
`save(destination: WritableText, text: String)` using the actual File service.
This checks structural conformance of a source-library method, rather than a
synthetic test-only algebra object.

Producer managed snapshot 3 accepted List aliases/snapshots and Map/Set custom
equality through all eighteen O0/O2/O3 source/`.mso`/`.mexe` routes, then exposed
a separate Regex factory bug: its new `pattern()` accessor shadowed the captured
String for sibling methods. The source-only parameter rename in `1d14693`
corrects that binding error; this is why source module checking alone does not
replace a real call through its returned object.

The unchanged collected outputs of those eighteen successful routes were retained
and independently compared in
`.temp/stdlib-upgrade/snapshot3-collections-accepted.json`. Another eighteen
successful routes cover environment snapshots and the concept-backed File/Command
program, including all optimization levels, source removal and changed launch
working directory. Their report is
`.temp/stdlib-upgrade/snapshot3-env-concept-services.json`. Together these are
thirty-six actual executions and 171 exact output-byte comparisons before the
remaining corrected Regex factory routes.

### Completed incremental acceptance

The corrected embedded Regex source in managed producer snapshot 4 accepted all
nine remaining O0/O2/O3 source/`.mso`/`.mexe` routes. Observations include the
scalar index 2 and length 1 for `β` after `A🙂`, named capture selection,
replacement text `A🙂[β]`, builder aliasing, independent construction and clearing.
`.temp/stdlib-upgrade/snapshot4-regex.json` records the exact component identities
and all nine successful executions.

The completed independent integration set comprises:

| Contract group | Accepted CLI executions | Exact output-byte comparisons |
| --- | ---: | ---: |
| List, Map and Set composition, producer 3 | 18 | 81 |
| Environment and concept-backed File/Command, producer 3 | 18 | 90 |
| Unicode Builder and corrected Regex factory, producer 4 | 9 | 81 |
| Actual SSH File/Command composition, producer 2 | 3 | 15 |
| Total | 48 | 267 |

Only the affected nine Regex routes were rerun after the source factory fix;
the previously accepted thirty-six local routes and unchanged SSH operations
were retained rather than blindly repeated. These are Linux managed-product
observations; Native AOT/platform packaging and paired native CLI latency are
separate delivery-owner acceptance, not implied by these results. The preserved
native baseline and opt-in benchmark are ready for that paired measurement.
