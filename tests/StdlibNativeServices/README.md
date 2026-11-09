# Native source service families

The focused journey executes the public named Scope families, not a second
service implementation. Instances are ordinary data records. Associated
operations take an explicit receiver and specialize with its selected policy.

- `files::File` and `files::Directory`: actual UTF-8 write, append, read,
  transfer and rename; shared aliases, fresh records, fixed Path storage,
  directory snapshots and shared default policy identity.
- A checked, source-defined in-memory `TextIOPolicy`: construction and signature
  checking execute no callbacks; read/write/append and line enumeration select
  only that policy and do not touch the physical path. The default line producer
  retains native streaming rather than buffering the whole file first.
- `commands::Command` and `commands::Completed`: actual owned child stdin,
  Unicode captured output, reusable argument snapshots, fresh configurations,
  and publication of completed output. Process identity and native control remain
  in the existing Process value.
- Wrong policy signatures, non-Path assignment and malformed process
  specifications fail at compile time with source-owned diagnostics.

The same programs run at O0/O2/O3 through source, serialized relocatable
bytecode and serialized context-bound executable bytecode. The canonical File
and completed-result records are checked to contain no per-instance method
closures. PascalCase family Scopes are the canonical API; instances contain
payload data, not callable adapters or method bags.

```sh
dotnet run --project tests/StdlibNativeServices -c Release \
  --artifacts-path .temp/native-services-build
```

Disposable fixtures stay under repository `.temp` and are removed by their live
owner. Coordinate product builds with other implementation owners, or build the
project in an isolated artifacts directory as above.

The initial managed service matrix passed 180 assertions with zero build
warnings/errors (2026-11-09). Following namespace consolidation, the focused
files/service CLI selection passed three O2 stages, 36 exact-byte observations
and three diagnostics (`.temp/native-services-final-split.json`), without
repeating the earlier matrix. Native Windows/Linux acceptance belongs to the
delivery journey.

## Selected command and exit policies

`--command-policies` selects the new process work only; it does not rerun the
180-assertion filesystem/service matrix above. A source-defined `ProcessRunner`
records actual dispatch while delegating to the native process primitives.
`with_args`, `append_args` and `on` preserve that runner and own fresh argument
snapshots. Construction and concept checking invoke no runner.

`commands::Checked` classifies actual completed data with an explicit `ExitPolicy`
and returns `data::Result`. The default preserves nonzero exit as an error value
containing stdout, stderr, code and actual Process. A custom policy accepting
exit 7 does not rewrite `success` or the real exit status. Missing runner/member
and incorrect return constraints are compile-time diagnostics.

```sh
dotnet run --project tests/StdlibNativeServices -c Release \
  --artifacts-path .temp/native-services-build -- --command-policies
python3 tests/Msync.EndToEnd/commands_policy_journey.py \
  --binary /absolute/path/to/msync
```

The CLI journey runs real UTF-8 child processes at each source/object/executable
stage, removes source before binary execution, changes the executable launch
directory, and verifies that an unused SSH command remains metadata. Python is
the explicitly selected test child executable, not an msync runtime dependency;
the report records its exact interpreter and version.

The final isolated managed build passed 114 selected process/exit-policy
assertions with zero warnings/errors. Real spawn returns before its stdin closes;
returned Process ownership crosses source function frames to its caller.
The actual CLI journey also passed nine O0/O2/O3 staged routes, 99 exact-byte
observations and four negative diagnostics. Exact managed image and Python child
identities are recorded in `.temp/commands-policy-final-managed.json`.
