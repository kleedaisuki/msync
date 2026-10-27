# Transaction and process-resource mediation for v0.4

Status: implementation design and evidence, not a claim that v0.4 is implemented.
The supplied v0.4 example is the language requirement. This note separates the
database model, filesystem publication, and child-process mediation because none
of those three mechanisms can replace the others.

## Existing foundation and the gap

`src/io/mod.rs::StagedFile`, `src/runtime.rs::copy_file`, and the stream executor
already stage individual native files before atomic pathname publication.
`src/plan.rs::Effects` already distinguishes reads, writes, consuming reads and
observable operations; `Resource::overlaps` includes tree prefixes and aliases.
The scheduler admits bounded co-start cohorts. Reuse these mechanisms.

They do **not** currently provide a multi-file transaction, retained old versions,
snapshot reads, savepoints, or rollback of a program's direct filesystem writes.
`docs/design/runtime.md` explicitly documents the existing per-leaf rather than
whole-tree guarantee. A transaction wrapper around the old executor would keep
publishing visible files too early. Copying a working directory and changing the
child's cwd would leave absolute-path writes, `..`, symlinks, inherited handles,
and descendant processes outside the claimed rollback boundary.

## Semantic model to implement

The transaction manager owns *resource versions*, not only a list of copied
filenames. A resource is a physical endpoint plus normalized path and exact/tree
scope, retaining identity aliases conservatively. A version is immutable file
bytes and metadata, a tree manifest, or an explicit absence tombstone. An absent
path is a value: restoring it means deleting a newly created entry, not leaving an
empty file. A tree replacement must also record removed descendants.

Proposed state ownership:

```text
ResourceStore
  committed epoch -> immutable versions / directory manifests
  path + identity conflict index
  transaction id -> TxnState

TxnState
  origin: retained pre-transaction view
  checkpoint: latest committed checkpoint
  pending: private version overlay since checkpoint
  reads: version + existence + directory-predicate observations
  isolation: ReadUncommitted | ReadCommitted | RepeatableRead | Serializable
  processes: owned process trees and IO drain tasks
  state: Active | Committing | Completed | Reverting | Failed
```

Use dense typed IDs for transactions, process instances and versions, rather than
confusing a user-visible binding name with runtime ownership. The compiler keeps
lexical bindings separately: transactions introduce a runtime context, not a
lexical scope. Resource capture and OS handles belong to the executor, never the
syntax AST.

### Checkpoints and named rollback

The user's `commit` is stronger than a mere parser marker but different from SQL
terminal COMMIT: it publishes a checkpoint while execution continues and the
transaction's **origin remains undoable** by a later top-level `rollback A1`.
Do not discard before-images at each checkpoint. Bare `rollback` discards changes
since the latest checkpoint, keeps prior checkpoints, and resumes in that same
context. Block exit publishes the final pending checkpoint. Another named
transaction cannot be rolled back from within a transaction.

This resembles savepoints only for the local rewind operation; PostgreSQL's
[SAVEPOINT contract](https://www.postgresql.org/docs/current/sql-savepoint.html)
restores the state after a mark, while ordinary SQL COMMIT ends the transaction.
The DSL deliberately needs its own lifecycle and retained undo history.

Named rollback restores the named transaction's enrolled write resources to
their origin versions as a new logically ordered operation, including origin
absence and deleted tree members. Do not erase unrelated files or rewind the
interpreter's entire environment. The concrete `src/transaction` version-store
implementation rejects origin rollback when a foreign committed version touched
the same key after transaction begin, even if our own later checkpoint is now the
latest version. This avoids silently erasing another transaction's changes.
The planner must therefore hold conflicting effects through a known named
rollback lifetime, including that rollback node's full origin-write domain;
execution cannot reorder overlapping updates into that interval. This policy
must remain explicit rather than depending on incidental undo-loop order.

### Four isolation levels

All reads first consult their own pending writes. A usable version must be
complete; read-uncommitted is not permission to expose a partially copied file.

| Level | View to select | Required conflict treatment |
| --- | --- | --- |
| Read uncommitted | Latest complete tentative or committed version permitted by logical scheduling | No dirty-write tearing; own writes remain private until publication |
| Read committed | Committed epoch captured for each statement, plus own writes | Never select another transaction's tentative version |
| Repeatable read | Transaction snapshot or retained per-key versions, plus own writes | Repeated exact resource reads stable; a full snapshot can also prevent phantoms |
| Serializable, default | Stable snapshot plus predicate/tree conflict validation, or strict locks | Prevent write skew and directory/existence phantoms, not only WAW conflicts |

The [PostgreSQL isolation documentation](https://www.postgresql.org/docs/current/transaction-iso.html)
defines the anomaly hierarchy and uses statement snapshots for read committed.
It intentionally maps read-uncommitted to read-committed; stronger guarantees at
a requested level are legitimate, but if msync deliberately implements such an
alias it must say so instead of advertising four distinct schedulers. The user
asked for four levels; keep four typed values and their explicit policies.

For a first correct scheduler, strict resource and tree-predicate locking is
simpler than implementing SSI from scratch. Read locks have statement lifetime
for RC, transaction lifetime for RR, and include tree/existence predicates for
serializable. Preserve write ownership until its publication decision. Acquire
static lock sets in a deterministic order; dynamic acquisitions require
deadlock detection or an ordered restart policy, **not** a retry that silently
reruns an irreversible child program. SQL Server's
[locking and versioning guide](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide)
is a concrete production reference for resource versus range locking.

Top-level source-order equivalence is stricter than generic serializability:
serializability alone permits another serial order. Retain a logical statement
ordinal and commit/order edges for overlapping observations. Disjoint transaction
regions can run concurrently. A rollback node touches the full retained write
domain, not just whatever happens to be in its latest pending overlay.

### Atomicity and persistence boundary

Publish a transaction's version-table epoch atomically to cooperating msync
readers. Physical export to several ordinary host pathnames is not one atomic
filesystem operation; external, nonparticipating readers can otherwise see a
prefix. Either keep all managed readers behind the view, use a directory-root
exchange when valid, or explicitly bound external observation guarantees.

Do not conflate recoverable in-process errors with power-loss durability. SQLite's
[atomic-commit design](https://sqlite.org/atomiccommit.html) shows why undo data,
flush ordering, a commit decision, and multi-file coordination are separate
operations. For msync, a small persistent journal can own the ordered publication
intent and completion receipts while immutable file objects hold payloads.
An unfinished journal must be recoverable before a new writer enrolls the same
resources. A SQLite dependency helps journal metadata; it does **not** make
external file renames or remote SCP commits transactional by itself.

## Program execution: classify and mediate resources

A program is a process instance with owned stdin/stdout/stderr and stable
post-exit `exit_code`, `pid`, and `meta` values. It is not a filesystem transport
disguised as another `Backend` string. Program lifetime and stdout draining must
run concurrently; waiting for process exit before draining stdout can deadlock on
one pipe buffer. stderr must be drained even when the script does not bind it,
with bounded spool storage rather than unbounded RAM. Close a finite stdin
producer when its transfer ends so a child waiting for EOF can terminate.

The relevant resource boundary is **all enrolled file/tree endpoints**, including
paths reached dynamically from a child, its cwd, explicitly typed path arguments,
and planned later observations. Loading an OS library, reading a locale, and
writing transaction-private scratch can be safely unrelated to these resources;
an absolute write under a managed tree cannot. A path passed as a string is not
a proof of a child's complete effect set.

Compiler effects should support:

```text
Known { reads, writes, predicates, consumes }
ManagedTreeUnknown { roots }
WorldUnknown
```

Unknown effects are proof barriers, not optimizer permission. Runtime mediation
can refine an unknown program after observing the actual accessed resources, but
must **intercept before mutation**, or hold a sufficiently broad private view
until validation. Discovering a write by polling after execution cannot prevent
dirty reads by concurrent managed readers. Broad process barriers are a correct
initial execution path, not a substitute for the requested later optimization.

Commit/rollback has to own process lifetime too. A still-running child must not
continue mutating a version after it becomes immutable. The simplest fence is:
close completed finite inputs, finish all connected drains, await the owned child
tree, then capture and publish. If the language allows a live program to span a
checkpoint, implement a real process-tree quiesce/lease handoff; merely copying
the overlay while it runs is not a snapshot. Process bindings can retain exit
metadata after the process has been joined, matching the example's later
`program::exit_code` transfers.

### Linux/WSL path

Preferred mechanism: a private mount/user namespace, immutable lower snapshot(s),
and transaction-private writable filesystem views. Bind each view at its original
absolute path in the child namespace; this handles absolute paths inside programs
without textual argument rewriting. Use a private PID namespace/process cohort,
close inherited writable host handles, mount `/proc` for that PID namespace, and
avoid making the live originals reachable via a second bind mount. Native `bwrap`
already supplies much of the namespace setup and descendant cleanup; its
[upstream contract](https://github.com/containers/bubblewrap/blob/main/README.md)
also makes clear that policy is the caller's responsibility.

OverlayFS is an acceleration, not the version database. Its
[kernel documentation](https://www.kernel.org/doc/html/latest/filesystems/overlayfs.html)
requires compatible upper/work filesystems and forbids changing an underlying
filesystem while it is mounted as an overlay layer. Therefore **do not mount the
live cwd as a lowerdir and concurrently publish commits into it**. Use immutable
snapshot/reflink objects or a view provider that answers through the version
store. Handle whiteouts, opaque directories and renames when extracting a delta;
walking only regular upper files loses deletes and directory replacement.

If unprivileged namespaces are unavailable, a mature userspace path mediation
backend is possible: [PRoot](https://github.com/proot-me/proot/blob/master/doc/proot/manual.rst)
implements path redirection with `ptrace`, including static executables, without
privileged mounts. It is not a complete security boundary by itself and carries
syscall interception costs. Use it only with an explicit supported policy and
benchmark after correctness. Prefer this to building a partial syscall tracer.

[Landlock](https://www.kernel.org/doc/html/latest/userspace-api/landlock.html)
provides unprivileged ambient-right restriction inherited by children; it can
prevent access outside the allowed view, but does not redirect paths or implement
MVCC. Probe ABI capabilities at runtime. A missing required write/truncate/rename
restriction must not silently become an isolation downgrade.

#### Implemented Linux view and payload identity boundary

`process::view_linux` now materializes complete directory snapshots into private
owned roots and binds them at their original absolute paths. The host tree is
read-only; `/tmp`, `/run`, `/dev` and `/proc` are namespace-local. PATH and HOME
are inherited; TMPDIR is explicitly `/tmp`. Export waits for the process tree
and physical output drains, and cancelled views are discarded rather than
exported as successful versions. Provider capability failures are not cached:
repairing dependencies or user-namespace policy can be retried in the REPL.
Successful probes retain the resolved provider executable.

Do not interpret bubblewrap's JSON `child-pid` as the target PID. In the pinned
[v0.9.0 implementation](https://github.com/containers/bubblewrap/blob/v0.9.0/bubblewrap.c),
the outside monitor reports the namespace child before that child forks the
PID-1 reaper and actual PID-2 program. The block-fd gate also precedes that fork;
polling its children is racy for short-lived targets. Keeping the normal reaper
avoids changing target signal/reaping semantics merely to make PID lookup easy.

An explicitly configured msync worker runs in the payload position, sends a
bounded readiness frame over an inherited private Unix stream, marks that
descriptor CLOEXEC, and execs the target with unchanged argv/environment/stdio.
The parent obtains its real host PID from kernel SCM_CREDENTIALS; SO_PEERCRED
would instead identify the socketpair creator. See [Unix credentials](https://man7.org/linux/man-pages/man7/unix.7.html)
and [PID-namespace translation](https://man7.org/linux/man-pages/man7/pid_namespaces.7.html).
EOF is the exec-success boundary; a failed exec transmits the native errno and
bounded detail separately. `RunningView` and `SealedView` scalar methods expose
payload PID and metadata, with namespace/supervisor IDs explicitly distinguished.
The low-level `Process` still owns the native monitor. Bubblewrap's shell-style
signal-to-exit-code normalization is named in metadata, not mislabeled as a raw
signal status.

The API requires all manager-enrolled resources visible to that process, not an
argv-derived subset: an omitted managed path remains readable through the host
tree, so its omission would not prove disjoint effects. Every supplied root is
conservatively Read+Write. Existing nonoverlapping directory roots and disjoint
scratch are the initial supported layout; dynamic enrollment/masked omitted
roots remain manager integration work. Read-only mounting is not a general
hostile-syscall or filesystem Unix-socket IPC security claim.

Focused local evidence: WSL UID 1000, bubblewrap 0.9.0, fixtures on a private
96 MiB native tmpfs mounted at project `.temp` in `unshare -Urnm`; source and
build cache stayed outside that mount. Six actual view journeys passed,
including `/proc/<payload-pid>/status` NSpid and cmdline checks against the
credentialled PID, absolute/relative/grandchild writes, frozen reads after a
host modification, host file/TCP denial, exec error versus nonzero exit, and
cancel-without-export. The targeted capability-cache regression and strict
library/binary/view-target Clippy passed. The same initial binary's directory
publication failed with EPERM on DrvFs, so that result is recorded as a
filesystem publication capability, not a namespace failure or a performance
measurement. The tests remain explicit until the runner provisions bubblewrap
and its namespace policy; skipped tests are not cross-platform completion.

### Native Windows path

Windows has no portable unprivileged mount-namespace equivalent that can simply
overlay an arbitrary original tree. Microsoft advises against new dependencies
on [Transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf);
it is also not a cross-backend transaction manager. [ProjFS](https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system)
is projection inside a virtualization root, not interception of arbitrary outside
path access. [WinFsp](https://github.com/winfsp/winfsp) is a mature user-mode
filesystem provider, but a mount alone does not force a process to use it.

Two realistic adapters have different deployment/assurance tradeoffs:

1. **Functional build-style mediation:** Microsoft's
   [BuildXL sandbox](https://github.com/microsoft/BuildXL/blob/main/Documentation/Specs/Sandboxing.md)
   uses enhanced DetoursServices for path redirection, access blocking, absent
   probes, enumeration/read/write reporting, and child process transitions. It
   reports 1--5% process-time overhead in its own workloads, not an msync result.
   This is a close production analogue of dynamic effect discovery. Study its
   complete native interception/manifest protocol or use a packaged supported
   executor rather than implementing only `CreateFileW`. Distinguish functional
   support for normal toolchains from a hostile-process security boundary.
2. **Installed virtualization middleware:**
   [Sandboxie-Plus](https://github.com/sandboxie-plus/Sandboxie) uses a driver and
   service to give Windows programs an isolated copy-on-write resource view.
   A dedicated box can preserve native absolute paths and descendants. It adds
   installation/service requirements, but avoids inventing a filesystem filter.
   [Its volume layout](https://sandboxie-plus.github.io/sandboxie-docs/Content/SandboxRootsVolumeLayout/)
   maps drive/profile/share namespaces to box storage. Importing its filesystem
   delta must include
   [Delete V2 tombstones and rename records](https://sandboxie-plus.com/sandboxie/delete-v2/),
   not only changed files. Use an msync-owned box and inspect effective access
   rules; inherited `OpenFilePath` rules can intentionally bypass virtualization.

Neither adapter should alter a user's existing sandbox definition. Sandboxie
[SbieIni](https://sandboxie-plus.github.io/sandboxie-docs/Content/SbieIniCommandLine/)
can query effective driver configuration; ordinary mutations affect shared INI
state, while `/drv` is an advanced distinct route with authorization limits.
Provision/cleanup of a unique box needs a deliberate lifecycle and actual driver
acknowledgement, not assuming `Start.exe /reload` exit zero proves policy applied.

An AppContainer plus explicit ACL grants can deny host originals while granting
the private resource view, but
[AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation)
does not itself remap original absolute paths. Combining it with a view provider
is possible; treating cwd substitution as that provider is not.

### Windows packaging investigation: what can actually be reused

The [official BuildXL installation instructions](https://github.com/microsoft/BuildXL/blob/main/Documentation/Wiki/Installation.md)
state that prebuilt engine binaries are distributed internally; external users
build the source. No public BuildXL processes package or release archive was
found in the scoped package/release queries. Do not make an invented NuGet
package a deployment requirement.

The official `SandboxedProcessExecutor` is real, but its
[argument parser](https://github.com/microsoft/BuildXL/blob/main/Public/Src/Tools/SandboxedProcessExecutor/Args.cs)
accepts `/sandboxedProcessInfo:<file>` (`/i`) and
`/sandboxedProcessResult:<file>` (`/r`), with optional access reporting. The input
is BuildXL's serialized `SandboxedProcessInfo`, not an ordinary JSON/argv API.
Using it unchanged requires its managed object model and deployment closure.

There is a native-only route. At inspected BuildXL commit
`4b61335169c612ef6ce78b9bf082501f0b9b6b83`,
[`DetoursServices.dsc`](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Sandbox/Windows/DetoursServices/DetoursServices.dsc)
builds `BuildXLNatives.dll` and injected x64/x86 `DetoursServices.dll` from a
bounded native source set, under MIT. Exported Windows-system-ABI entrypoints
include:

```text
DetouredProcessInjector_Create(&GUID, remote_injector_pipe, report_pipe,
                              dll_x86_ansi, dll_x64_ansi,
                              payload_size_u32, payload_bytes) -> injector*
CreateDetouredProcess(command_utf16, flags_u32, environment_block,
                     cwd_utf16, stdin, stdout, stderr, job, injector*,
                     &process_handle, &thread_handle, &pid_u32) -> status_i32
DetouredProcessInjector_Inject(injector*, pid_u32, inherited_handles_bool)
DetouredProcessInjector_Destroy(injector*)
```

The exact declarations and managed-side call contract are in
[`DetouredProcessInjector.cpp`](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Sandbox/Windows/DetoursServices/DetouredProcessInjector.cpp),
[`DetoursServices.h`](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Sandbox/Windows/DetoursServices/DetoursServices.h),
and [`ProcessUtilities.Win.cs`](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Utilities/Native/Processes/Windows/ProcessUtilities.Win.cs).
Status distinguishes creation, injection, job assignment, handle inheritance,
resume, payload-copy and process-attribute failures. Treat each failure as
failure, never retry an already potentially running untracked program.

However, this is **not a ready transactional overlay**. In the public build,
`FEATURE_DEVICE_MAP` is disabled; `RemapDevices` returns an invalid handle.
`DirectoryTranslator` handles path normalization/reporting and returned paths;
`Detoured_CreateFileW` still passes `lpFileName` to `Real_CreateFileW`. The
[`DeviceMap.cpp` external branch](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Sandbox/Windows/DetoursServices/DeviceMap.cpp)
and actual
[`DetouredFunctions.cpp` call site](https://github.com/microsoft/BuildXL/blob/4b61335169c612ef6ce78b9bf082501f0b9b6b83/Public/Src/Sandbox/Windows/DetoursServices/DetouredFunctions.cpp)
are the deciding evidence. Reusing native enforcement/injection would still
require a tested private path-view implementation of our own.

Upstream [Microsoft Detours](https://github.com/microsoft/Detours) is independently
MIT and available as a [vcpkg port](https://github.com/microsoft/vcpkg/tree/master/ports/detours).
The inspected port pins source commit
`9764cebcb1a75940e68fa83d6730ffaf0f669401` with a source hash and builds a static
library. `DetourCreateProcessWithDllExW`, `DetourCopyPayloadToProcess`,
`DetourFindPayloadEx`, and `DetourRestoreAfterWith` support an msync-owned native
shim; [official samples](https://github.com/microsoft/Detours/blob/main/samples/README.TXT)
demonstrate injection and child propagation. This avoids drivers, not the work
of implementing and validating filesystem mediation.

The external repository `soda0289/bazel-sandbox-windows` demonstrates small native
packaging, but is **not an adoptable dependency**: its
[license section](https://github.com/soda0289/bazel-sandbox-windows#license)
explicitly grants no license to its original launcher, overlay and build code.
Its documented reparse-point and short-name escapes and removed reporting
machinery also preclude treating its overlay as production-validated for msync.
Do not copy those original files or its manifest serializer into this project.

### Recommended mature Windows adapter: dedicated Sandboxie view

For the current project, use an explicitly provisioned Sandboxie backend rather
than rush an incomplete NT-hook implementation. This keeps native Windows
executables supported without claiming that every host already has the
dependency. Missing service/driver is a precise capability error with installation
instructions, not a silent Linux substitution or unisolated execution.

The key policy is `WriteFilePath=<managed-root>`, **not** `OpenFilePath`.
[WriteFilePath](https://sandboxie-plus.github.io/sandboxie-docs/Content/WriteFilePath/)
makes the selected namespace box-only: ordinary opens cannot fall back to host
contents, but existing sandbox copies remain usable. Seed complete immutable
transaction roots into this private namespace before starting the user program.
This avoids a mutable-host-lower-layer problem and preserves the program's
original absolute paths. Pre-seeding a full tree is an intentionally simple
correctness baseline; optimize snapshot population later.

Use a unique owned name within the 40-WCHAR box-name ABI, for example
`msync_<random-hex>`, and a unique root under the project's
`.temp/transactions/<run>/sandbox`. Configure only that new section:

```text
Enabled=y
FileRootPath=<absolute project-owned sandbox root>
AutoRecover=n
AutoDelete=n
UseFileDeleteV2=y
WriteFilePath=<each enrolled managed root>
OpenPipePath=\Device\NamedPipe\<unique msync control channel>
```

Do not add broad `OpenFilePath`/`OpenPipePath` rules for user data. They are direct
access exceptions, not redirection. Normal host libraries/locale reads outside
managed roots can use normal sandbox behavior. Do not require premium privacy
or rule-specificity features for a basic adapter. Conflicting global/template
direct-access rules must be detected or excluded from the supported effective
policy; a successful setting write is not proof that the intended policy wins.
Keep the helper executable accessible outside enrolled roots or through one
explicit read-only internal-artifact exception, not a broad writable exception.

**Avoid depending on physical box layout or tombstone formats.** Run an msync-owned
worker inside the box to seed resources via logical paths, launch the actual
program, and later enumerate/export its complete logical managed trees. Compare
the final manifest against the initial version-store snapshot to derive deletes
and replacements. This delegates Delete-V2/rename/merged-view semantics to the
installed filesystem implementation. The storage's drive/user/share mappings and
`FilePaths.dat` can change independently of our version database.

The worker communicates through one bounded, framed named pipe with an exact
[OpenPipePath grant](https://sandboxie-plus.github.io/sandboxie-docs/Content/OpenPipePath/).
It opens ordinary local stdin/stdout/stderr pipes for its child and multiplexes
payload/status across that control link. This is important because the pinned
[`SbieDll_RunSandboxed` implementation](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/dll/callsvc.c)
serializes `STARTUPINFO.dwFlags`/`wShowWindow` but **not its standard handles**
across the service request. Redirecting `Start.exe` itself is not evidence that
the actual child receives our pipes. Authenticate the worker handshake and box
membership; enforce message-length, resource-root and payload-memory limits.

Native integration can load installed `SbieDll.dll` by its absolute installation
path and resolve these pinned exports from
[`sbiedll.h`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/dll/sbiedll.h)
and [`sbieapi.h`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/dll/sbieapi.h):

```text
SbieDll_RunSandboxed(box_utf16, command_utf16, cwd_utf16, flags_u32,
                    STARTUPINFO*, PROCESS_INFORMATION*) -> BOOL
SbieApi_GetVersionEx(version_utf16_16, &abi_u32) -> NTSTATUS
SbieApi_QueryBoxPath(box_utf16_40, file*, key*, ipc*,
                    &file_bytes_u32, &key_bytes_u32, &ipc_bytes_u32) -> NTSTATUS
SbieApi_EnumProcessEx(box_utf16_40, all_sessions_u8, session_u32,
                     pids_u32*, &capacity_or_count_u32) -> NTSTATUS
SbieDll_KillAll(session_u32, box_utf16) -> BOOLEAN
```

Use `extern "system"`, correct one-byte `BOOLEAN` versus four-byte `BOOL`, padded
box-name inputs where specified, and byte capacities for QueryBoxPath. The
[API documentation](https://sandboxie-plus.github.io/sandboxie-docs/Content/SBIEDLLAPI/)
warns that ABI/authorization context matters. Keep live process handles rather
than treating a PID list as stable ownership. Enumeration may truncate at its
capacity even on success; grow/recheck before accepting an empty/stable cohort.

CLI launch/provisioning remains useful for manual setup:

```text
SbieIni.exe set <owned-box> Enabled y
SbieIni.exe set <owned-box> FileRootPath <owned-root>
SbieIni.exe set <owned-box> AutoRecover n
SbieIni.exe append <owned-box> WriteFilePath <managed-root>
SbieIni.exe queryex <owned-box> FileRootPath
Start.exe /box:<owned-box> /silent /wait <worker.exe> <control-channel>
Start.exe /box:<owned-box> /listpids
Start.exe /box:<owned-box> /terminate
```

`/wait` waits for the launched process, not a transaction's entire descendants;
`/terminate` requests service termination and zero is not completion evidence.
Join/verify the owned cohort before export or rollback, and never call
`/terminate_all` on a user's machine. Do not use `/keep_alive`: automatic restarts
could duplicate user work. Use normal section-scoped configuration updates and
read back the driver state; `/drv` requires a registered session-leader identity
and is not simply a no-persistence alternative available to an elevated CLI.
Cleanup removes only the owned section/root after process termination is verified;
it must not reload/clear unrelated sandboxes or assume asynchronous deletion has
finished.

### Pinned Windows Actions provisioning and remaining proof obligation

The inspected official release is
[Sandboxie Plus v1.18.8 / Classic 5.73.8](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.8),
published 2026-10-17. Its
[expanded asset list](https://github.com/sandboxie-plus/Sandboxie/releases/expanded_assets/v1.18.8)
publishes the x64 installer digest:

```text
https://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.8/Sandboxie-Plus-x64-v1.18.8.exe
SHA256 f27078ff74a07906fd51476079e1a7a8f5e20c8bb75a9d2ae86169d47179ae76
```

Provision only an ephemeral GitHub Windows runner for the integration job. Verify
the pinned SHA-256 and Authenticode status before running the installer. The
[pinned Inno Setup script](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Installer/Sandboxie-Plus.iss)
installs `SbieDrv` as a demand-start kernel driver and `SbieSvc` as an auto-start
service, then starts the service. A candidate unattended invocation is
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=""`, launched hidden with
`Start-Process -Wait -PassThru -WindowStyle Hidden`; retain the installation log.
Do not request ImDisk or launch SandMan UI. Check installer status, running
driver/service, exported API version and a real worker handshake. A reboot or
driver-policy failure must fail the meaningful adapter gate, not become a skip
reported as native support.

This provisioning path is source-verified but **not yet executed in Actions**;
driver compatibility, policy behavior, worker IO, exact absolute-path snapshots,
delete/rename export and child-tree termination still need the actual integration
journeys. Nothing was downloaded or installed as a driver on the developer's
Windows machine during this investigation. A dependency capability diagnostic
is correct locally, but release completion requires the provisioned native Windows
journey to pass elsewhere.

### Mechanisms that cannot serve as the correctness foundation

| Tempting mechanism | Why insufficient | Valid use |
| --- | --- | --- |
| `notify`, inotify, ReadDirectoryChangesW | Mutation already occurred; queues can overflow | Invalidation hint, UI observability |
| fanotify permission callback alone | Not a version view; not all mutations can be brokered; privilege/ABI limits | Antivirus-style access gating and observations |
| LD_PRELOAD only | Misses static binaries/direct syscalls; no complete child guarantee | Optional trusted-tool fast path after coverage tests |
| Seccomp user notification followed by CONTINUE | Mutable pointer arguments race the decision | Broker operations on private copies with separate kernel restriction |
| Hash cwd before/after | Misses outside/absolute writes and transient effects | Delta validation inside a genuinely isolated view |
| Copy/reflink file and keep writable aliases | Aliases can mutate a supposedly immutable snapshot | Copy-on-write storage without shared writable inode identity |

The [fanotify manual](https://man7.org/linux/man-pages/man7/fanotify.7.html)
describes notification/permission distinctions and event loss limitations;
[ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw)
explicitly discards its buffer on overflow. The
[seccomp notification manual](https://man7.org/linux/man-pages/man2/seccomp_unotify.2.html)
warns that CONTINUE decisions over mutable tracee pointer arguments cannot be a
standalone security policy. These are correctness constraints, not reasons to
avoid useful runtime instrumentation.

## Irreversible and remote effects

stdin written to a private child is contained only if its consequential resource
effects are mediated. stdout/stderr spool files and exit metadata are reversible
managed values; bytes already sent to an external socket, an email service, or a
remote command with unrestricted credentials are not undone by restoring a file.
Do not call an outbox flush rollback-safe after its receiver consumed the bytes.

For relevant non-file resources, use a transactional peer/broker with prepare and
commit/abort, or explicit compensating operations with their weaker contract.
For SSH/WSL files, a remote helper can own staged immutable versions, journal and
commit acknowledgements; sequential SCP calls alone do not provide multi-file
atomicity or cross-process isolation. Keep endpoint capabilities typed so
unsupported transactional effects are diagnosed *before* an irreversible action.
This must not regress existing top-level stream transfers outside transactions.

The requested application-level workaround is a version-store/view broker and
resource protocol where the OS does not expose sufficient transactional APIs.
It is not a claim that arbitrary distributed side effects become reversible.

## Integration interfaces

Keep these responsibilities separate even if initially modules in one crate:

```text
txn::Manager
  begin(id, isolation, logical_ordinal, enrolled_effects) -> TxnHandle
  begin_statement(txn, ordinal) -> StatementView
  read(view, resource) -> VersionLease
  stage(view, resource, replacement_kind) -> WritableLease
  checkpoint(txn, joined_process_receipts) -> CommitReceipt
  rewind_checkpoint(txn) -> RewindReceipt
  restore_origin(completed_txn, ordinal) -> CommitReceipt

process::Executor
  capabilities(endpoint) -> MediationCapabilities
  spawn(ProgramSpec, ResourceView, cancellation) -> ProcessHandle
  join_tree(handle) -> ProcessReceipt { actual_effects, exit, captured_ports }

view::Provider
  materialize(statement_view, required_roots) -> PrivateView
  seal(private_view, process_receipt) -> ValidatedDelta
```

The receipts carry proof-relevant epochs/resources and diagnostic provenance,
not just booleans. Program spawn establishes its unknown-effect barrier before
starting the child. Source file spans should survive to denied path access,
conflicting commit, unsupported backend capability and rollback failures.

## Discriminating verification, not a redundant full suite

1. Tiny model tests enumerate interleavings over two/three resources and compare
   permitted histories for RU/RC/RR/serializable; include write skew, absent-file
   probes and regex/directory phantoms. A test that only accepts isolation names
   proves nothing about isolation.
2. Filesystem journey: write A, checkpoint, write B, bare rollback, block exit,
   named rollback. Verify original bytes, original absence, removed descendants,
   metadata, no orphan stages, and meaningful failure recovery.
3. Run an actual child that writes by relative path, absolute path, `..`, mmap,
   rename/delete and a grandchild. Hold a barrier mid-write while another managed
   transaction reads. Verify no leak at the promised level and exact rollback.
4. Exercise process IO above pipe capacity with both stdout/stderr active, finite
   stdin EOF, nonzero exit metadata, cancellation and descendant cleanup. No test
   should depend only on a mocked spawn returning an exit code.
5. Force a denied managed path and a deliberately unrelated library/locale read;
   assert precise path, operation, program source span and capability advice.
6. Inject publication failure after the first physical export and recover from
   journal; never delete a journal merely to make a retry pass.
7. Run platform adapter journeys in GitHub Actions, with an explicitly provisioned
   Windows mediation backend and Linux user namespace prerequisites. Do not
   count skipped backend tests as cross-platform completion.

After integration, measure cold/warm process startup, checkpoint/rollback latency,
small-tree view creation and conflict-free concurrency in milliseconds. Attribute
snapshot bytes, file opens, predicates validated, lock waits and intercepted calls
separately. Reflinks/private overlays and indexed resource predicates are promising
optimizations; first preserve the sequential reference and actual child behavior.

## Local capability evidence (2026-10-27)

The development WSL `Ubuntu-24.04` reports kernel
`6.18.33.2-microsoft-standard-WSL2`, UID/GID 1000, and user namespaces enabled.
This scoped probe completed successfully:

```sh
unshare -Urnm sh -c 'mount --make-rprivate / && echo namespace_mount_ok'
bwrap --ro-bind / / --unshare-user --unshare-pid --unshare-net \
  --proc /proc --dev /dev /usr/bin/true
```

`bwrap` reported 0.9.0; `fuse-overlayfs` reported 1.13-dev with libfuse 3.14.0.
These probes establish usable namespace primitives, **not** a completed isolated
transaction view. They made no persistent host mounts or configuration changes.
Sandboxie/WinFsp commands/services and conventional installation paths were absent
on the Windows host. A Windows adapter therefore needs deliberate provisioning;
it must not be treated as an already verified local capability.
