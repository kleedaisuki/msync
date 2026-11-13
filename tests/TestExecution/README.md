# Concurrent real-process test execution

Run existing journeys with one product image and a shared 12 CPU / 24 GiB budget:

```sh
python3 scripts/test.py --list
python3 scripts/test.py
python3 scripts/test.py --binary PATH_TO_NATIVE_MSYNC
python3 scripts/test.py --journey cli --journey lsp
```

The default focused set is `semantic`, `artifact`, `lsp`, not every test project.
`--jobs` defaults to 12; CPU/memory reservations and resource locks determine
actual concurrency. `--cpus` and `--memory-mib` can reduce the shared ceiling.
Linux cgroup v2 and a systemd user manager are required for this runner. Existing
journey entry points remain usable independently and on other platforms.
`scripts/check_local.py` uses this runner on Linux, preserving its explicit
journey selection and existing formatting checks; non-Linux behavior remains
serial.

## Builds and Roslyn

Without `--binary`, use the ordinary **MSBuild incremental build** in
`artifacts/test-execution/build-tree/`. There are no custom source/environment
fingerprint keys, copied-image hit rules, or cached passing test results.
MSBuild decides which targets need rebuilding. `--binary` skips the product
build entirely. Each run copies the resulting payload once, so its journeys do
not race with a later build. Payload hashes identify what ran; they do not
control rebuilding.

Shared Roslyn compilation is enabled. A framework-owned SDK-specific systemd
service runs `VBCSCompiler` with a named `SharedCompilationId`, under the same
`msync-tests.slice` as the run scopes. The slice enforces the **combined** budget,
including the persistent compiler. Successful runs preserve the warm server;
Roslyn's normal idle lifetime releases it later. Other projects' default compiler
servers are not moved or shut down. Compiler PID, attachment/reuse, CPU/memory
accounting and actual client protocol logs are retained. A compiler task timeout
or cancellation stops this owned service to release its outstanding work.

Selected C# executable contract projects that already support `FrozenAssemblies`
can build against the same fixed managed product DLLs:

```sh
python3 scripts/test.py --binary PATH_TO_MANAGED_APPHOST \
  --project tests/HirEditorSyntax/Msync.HirEditorSyntax.Tests.csproj
```

The project adapter uses isolated test outputs, records build/run phases and
checks copied product DLL identity. Native AOT payloads without managed DLLs are
not managed test reference assemblies. `--prepare-only` builds/freezes an image
without selecting journeys.

## Results, failures and replay

Each new run directory under `.temp/test-execution/` contains:

- `result.json`: configuration, image identity, task outcomes and resource data.
- `results.sqlite`: task specs, queue/run intervals, concrete outcomes and phase
  decisions; full stdout/stderr, declared receipts and compiler-client logs are
  streamed into `output_chunks` as raw bytes in 64 KiB chunks.
- `image/`: the fixed apphost and dependencies.
- `tasks/NNN/`: independent working directories, logs and retained fixtures.

One controller owns the SQLite writer. Task completion and its byte logs commit
transactionally; WAL permits analysis readers. Task statuses distinguish
`passed`, `failed`, `timeout`, `cancelled`, and `infra_error`. No automatic retry
relabels an earlier failure. By default independent work drains after a failure;
`--fail-fast` cancels the remaining owned work and still returns failure (1),
not user-cancellation (130).

```sh
# Defaults to non-passing tasks; their original commands get new work directories.
python3 scripts/test.py --replay .temp/test-execution/OLD/results.sqlite
# Explicitly rerun one task, including a previously passing task.
python3 scripts/test.py --replay .temp/test-execution/OLD/results.sqlite --task lsp
```

Replay reuses the old fixed product unless a new `--binary` is supplied. It does
not overwrite the original run. Task scripts remain normal repository scripts;
replay after fixing a test uses the corrected script and preserves old results.

```sql
SELECT name,status,queue_ms,elapsed_ms,exit_code,error FROM outcomes;
SELECT p.name,t.name,p.details_json FROM phases p
LEFT JOIN tasks t ON t.id=p.task_id ORDER BY p.id;
SELECT task_id,stream,offset,length(data) FROM output_chunks;
```

Concatenate chunks in offset order to recover a stream byte-for-byte, including
NUL and invalid UTF-8. CPU counters for the shared compiler belong to the peer
service, not every overlapping client task; do not add its inclusive counters
once per task. Shared-slice counters are lifetime snapshots before/after the
run, while per-task cgroup counters describe that task's owned process tree.

## Task adapters

For an existing script not in the small catalog, supply argv in a JSON plan:

```json
{
  "tasks": [
    {
      "name": "my-journey",
      "command": ["python3", "{root}/tests/MyJourney/run.py", "--binary", "{binary}"],
      "cpu": 2,
      "memory_mib": 1024,
      "timeout": 120,
      "locks": ["owned-ssh-fixture"]
    }
  ]
}
```

Use `--plan FILE`. Arguments substitute `{root}`, `{binary}`, `{assemblies}` and
`{work}` without shell interpolation. `cwd` defaults to `{work}`; optional
`environment` contains key/value pairs. A declared `receipt` is retained as raw
bytes; zero exit without that file is an infrastructure error, not a pass.
`uses_compiler: true` connects a custom build task to the supervised compiler.
`exclusive: true` reserves the whole budget; later tasks cannot overtake a
waiting exclusive reservation.

The `benchmark` catalog task is exclusive and keeps the existing benchmark's
own measurement boundaries. Its throughput child reuses the admitted cgroup
rather than launching a sibling scope outside the shared budget. Functional
journeys never run alongside its timed work. The compiler service is frozen
through cgroup.freeze during exclusive non-compiler work and resumed afterward,
so its background GC cannot perturb measurements and its warm state survives. Task elapsed times are orchestration
data, not product latency benchmark results.

## Focused acceptance

```sh
python3 tests/TestExecution/acceptance.py --binary PATH_TO_NATIVE_MSYNC \
  --work .temp/test-execution/NEW_ACCEPTANCE_DIRECTORY
```

The real-process journey checks concurrent overlap, resource-lock serialization,
exclusive measurement ordering, actual native compiler errors, missing executable
classification, raw byte logs, replay, fail-fast, timeout, SIGTERM forwarding,
detached descendants, and repository-wide admission across two controllers.
`--case controls|receipts` selects an affected portion. No timing threshold or
CI gate is introduced. Implementation and retained findings are described in
[the execution note](../../docs/design/test-execution-economics-v090.md).
