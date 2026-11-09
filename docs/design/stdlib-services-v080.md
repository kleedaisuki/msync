# Native file and command families

Named Scope families own constructors and associated operations; anonymous
Scope records own instance data and selected policies. Functions take `self`
explicitly. The source modules are executable compiler input, not parallel
documentation or host-side object wrappers.

## Files: `files::File` and `files::Directory`

```msync
let file = $std::files::File::new(./report.txt);
$std::files::File::write(file, "completed");
let alias = file;
$std::files::File::move_to(alias, ./finished.txt);
let text = $std::files::File::read_text(file);
```

`File` records contain `path: Path` and a `TextIOPolicy` value. `PathRecord`
constrains path-only operations; `TextFile` additionally requires the policy.
`File::with_policy(path, policy)` selects read, line-production, write and append
behavior. `NativeText` invokes the existing IO primitives. A different policy
can apply a codec or an application store without another runtime resource
kind or a hidden global backend registry. Its `lines(Path) -> Range<String>`
operation preserves streaming in the native implementation instead of buffering
the entire file through `read` first.

`Directory::new(path)` stores only a Path. Its enumeration functions retain
ordinary lazy Range demand; `snapshot(self)` explicitly materializes one
observation. Construction neither opens a file nor observes or authenticates an
endpoint. Path identity/provenance, normal transfer failure and retry behavior
remain owned by existing IO and transport mechanisms.

Mutation changes the existing instance record, so aliases observe the same Path
while separate constructors remain independent. `move_to` changes `self::path`
only after a successful rename. There is no per-instance getter/method closure
table and no stale metadata cache.

## Commands: specifications, launch and completion

`commands::Command::new(executable, arguments)` stores an executable Path, one
materialized argument snapshot and the shared `NativeProcess` runner. It does
not start a process, inspect a remote filesystem or authenticate a connection.
`with_runner(executable, arguments, runner)` selects a `ProcessRunner` policy:

| Operation | Required result and lifetime |
| --- | --- |
| `spawn(Path, Range)` | A real live, controlled Process |
| `run(Path, Range)` | A completed Process with input closed |
| `run_input(Path, Range, String)` | A completed Process after supplying and closing input |

The compiler checks all three signatures at specialization. A policy can add
tracing, argument transformation or execution conventions by composing the same
native process primitives; it cannot fabricate a portable PID or substitute a
non-Process result. Runtime control, completion and transport lifetimes remain
owned by the returned Process.

A live Process returned through a source method transfers to the caller's work
ownership rather than joining before the caller can supply or close stdin.
This applies through the selected source runner and the associated Command
function; unrelated, nonescaping work still joins its original function owner.

`with_args`, `append_args` and `on(connection)` return fresh specifications and
preserve the exact selected runner. Old argument snapshots are unchanged.
`on` uses normal `Path @ Connection` qualification, not string prefix guessing.
The specification's fields have fixed storage types; aliases retain ordinary
Scope identity. The runner is explicit state, not a callback bag copied into
each instance.

`run`, `spawn` and `run_input` return Process values. `capture` and `capture_input`
return `Completed` data records with `process`, `output`, `error`, `code`, and
`success` fields. Strings belong to that run, and the actual Process is retained.
`Completed::lines(self)`, `error_lines(self)` and `write_to(self, destination)`
are shared associated functions. There is no global last-result slot.

## Exit classification: `commands::Checked`

Launching a command and interpreting its exit status are independent policies.
`Checked::new(command)` selects `ExitStatus`, which accepts ordinary successful
completion. `Checked::with_policy(command, policy)` selects an `ExitPolicy`
whose `accept(CompletedProcess) -> Bool` signature is checked statically.

```msync
scope SearchExit {
    accept: fn(result: $std::concepts::CompletedProcess) -> Bool {
        result::code == 0 or result::code == 1
    },
};
let command = $std::commands::Command::new(./search, ["needle"]);
let checked = $std::commands::Checked::with_policy(command, SearchExit);
let outcome = $std::commands::Checked::run(checked);
```

`run` and `run_input` return `data::Result`: accepted completion is `ok`, rejected
completion is `err`. Both variants retain the entire completed record, including
stdout, stderr, exit code and Process identity. A nonzero exit code is result
data, not a swallowed exception. Spawn, transport and IO failures retain their
original source diagnostics; an exit classifier does not catch or replace them.
Construction and type checking execute neither the runner nor the classifier.

`io` and `process` retain precise low-level primitives. `files` and `commands`
own their native Scope families; there are no duplicate factory implementations,
legacy entrypoint adapters or implicitly callable class Scopes.

## Focused validation

The canonical File/Directory/Command/Completed implementation initially passed
180 focused real filesystem, custom-policy and child-process assertions at
O0/O2/O3 through source and serialized object/executable bytecode. Subsequent
namespace-only cleanup reuses those results where the implementation is
unchanged. The command-policy entrypoint in `tests/StdlibNativeServices` selects
only launch/classification changes: real input/output, nonzero exits, custom
runners, preserved argument/policy snapshots, source-free stages and useful
signature diagnostics. Its 114 assertions passed through all three stages at
O0/O2/O3, including an actual returned live spawn whose caller closes stdin.
`tests/Msync.EndToEnd/commands_policy_journey.py` additionally passed nine actual
managed CLI routes, 99 exact output observations and four useful policy
diagnostics. It checks no effects during construction/check/compile/bind, no
authentication for an unselected SSH specification, deleted source files and
executable execution from another directory. Its report records the matched
assemblies and Python fixture identity. The same script provides the narrow
Native platform boundary; the delivery record owns those image identities and
results.
