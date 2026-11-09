# Source-composed file and command services

`io` and `process` organize associated operations in named Scopes without
another host resource type. Canonical instances contain data, not a collection
of captured getter and method functions. The existing filesystem, UTF-8 IO,
Range and Process primitives remain the effectful boundary.

## Canonical associated operations

```msync
let file = $std::io::File::new(./report.txt);
$std::io::File::write(file, "completed");
let alias = file;
$std::io::File::move_to(alias, ./finished.txt);
let text = $std::io::File::read_text(file);
```

The record has a `path` field and a `policy` field. `TextFile` composes the
`PathRecord` and `TextIOPolicy` requirements; operations that only need a path
accept the smaller `PathRecord` contract. `File::with_policy(path, policy)`
selects read/line-production/write/append behavior explicitly. The named `NativeText`
policy invokes the established IO primitives. A policy can instead apply a
codec or implement an application store without changing the File family or
pretending that a different runtime resource kind has been introduced.
Line production is its own policy operation so the native default preserves
streaming reads instead of buffering an entire file through `read` first.

`Directory::new(path)` stores only its Path. Directory operations take that
record explicitly and retain ordinary lazy enumeration versus eager snapshot
semantics. `Command::new(executable, arguments)` stores the executable Path and
one materialized argument snapshot. `Command::run(self)` returns a real Process;
`Command::capture(self)` returns a completed data record with `process`,
`output`, `error`, `code`, and `success` fields. `Completed::lines(self)` and
`Completed::write_to(self, destination)` are shared associated functions, not
closures allocated into every result.

Construction does no filesystem observation, opens no file, and starts no
process or SSH authentication. Mutation changes the existing anonymous Scope
record, so aliases see the same Path or configuration; fresh constructors do
not share instance state. `move_to` updates `self::path` only after the rename
succeeds. Policy and data field storage types remain fixed.

## Compatibility callable services

The following factory-style API remains supported for existing source. New code
uses the named families above; these older contracts are not a template for
adding further getter/method bags.

## File and directory ownership

`$std::files::File(path)` returns a fresh anonymous Scope whose methods share a
captured Path location. `path()`, `name()`, `extension()` and `parent()` inspect
that current location. Reads, writes, metadata, permissions and link creation
are performed only when the corresponding method is called. No file is opened
by construction and no status is cached as a later proof.

`move_to(destination)` performs the rename before updating the captured location.
Aliases of one File observe the new location; separately constructed File values
do not. The captured storage type remains Path. `copy_to` leaves the source
location unchanged and uses ordinary transfer semantics, including endpoint
provenance, atomic file replacement and retryable directory cohorts.

`Directory(path)` similarly owns a current location. `child(name)` returns a
Path; `file(name)` builds a File service. `entries()`, `walk()`, `files()`,
`directories()` and `matching(predicate)` are Range observations, not cached
lists. Filtering preserves normal lazy demand. `snapshot()` explicitly
materializes one directory observation. No global filesystem registry or extra
validation pass is introduced.

This follows the useful object-location aspect of
[.NET FileInfo](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileinfo)
and the demand-driven behavior of
[DirectoryInfo.EnumerateFiles](https://learn.microsoft.com/en-us/dotnet/api/system.io.directoryinfo.enumeratefiles),
without inheriting a stale metadata cache or introducing a second Array API.

## Commands and completed results

`$std::commands::Command(executable, arguments)` captures a Path and one
materialized argument Range. Argument evaluation happens once at construction;
subsequent runs reuse that snapshot. Construction does not start a local process,
authenticate an SSH connection or observe the remote filesystem.

`with_args`, `append_args` and `on(connection)` return fresh configurations,
leaving the original executable and argument snapshot unchanged.
`executable()` and `arguments()` inspect that captured configuration; detached
mutable data fields do not masquerade as live configuration setters. `on` uses
`program @ connection`, preserving the selected endpoint's grammar. `spawn`
returns the actual controlled Process; callers may use `$std::process` to write
stdin, close it, wait, terminate or kill. `run` closes stdin and waits;
`run_input` writes the supplied text, closes stdin and waits.

`capture` and `capture_input` produce a fresh completed Result record:
`process`, `output`, `error`, `code`, `success`, plus `lines()`, `error_lines()`
and `write_to(destination)`. Strings belong to that completed run, not to a
mutable global last-result slot. `output()` and `lines()` on Command perform a
new run on each invocation. Nonzero exit codes remain normal result data; spawn,
transport and IO failures retain their original source diagnostics.

The separation of reusable configuration from started Process follows
[Java ProcessBuilder](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/ProcessBuilder.html).
There is deliberately no source-level fake streaming pipeline: buffering one
completed output is not presented as a concurrent OS pipe.

## Validation

`tests/StdlibServices` exercises real UTF-8 read/append/copy/rename, alias identity,
fresh factory instances, directory filtering and explicit snapshots, and a real
child process receiving stdin. The same source runs at O0/O2/O3 through direct
modules, serialized relocatable IR and contextual executable IR. Fixtures live
under repository `.temp` and are removed by their live test owner.

The focused managed journey passed 72 assertions at O0/O2/O3, each through source,
serialized `.mso` and contextual `.mexe`. Command coverage includes a real owned
child stdin pipe, Unicode output publication, `append_args` preserving the
original snapshot and `on($local)` selecting the explicit endpoint. The final
incremental harness build used `BuildProjectReferences=false` to consume the
shared product owner's coherent compiler/runtime outputs.
