# Windows resource-view bridge

Status: implementation exists and local protocol/capability gates pass. The
signed-driver journeys have **not** been run on the development host; no driver,
service or Sandboxie configuration was installed or changed there. The ephemeral
Windows Actions gate must prove native support before release acceptance.

## First provisioned-driver result and path-policy correction

[Actions run 40498380133](https://github.com/kleedaisuki/msync/actions/runs/40498380133)
successfully installed and loaded the pinned signed driver/service and passed its
ABI probe. All four native journeys then stopped before launching a worker at the
`FileRootPath` effective-setting check. This is an adapter verification defect,
not a failed driver installation or evidence that isolated payload IO passed.

The pinned
[`Conf_Api_Query`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/drv/conf.c)
expands values unless `CONF_GET_NO_EXPAND` is present. Its
[`Conf_Expand_Helper`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/drv/conf_expand.c)
calls `File_TranslateDosToNt` for DOS paths before expanding variables. Comparing
an expected DOS/verbatim string with the expanded NT device string is therefore
incorrect even when the setting is exactly right. `WriteFilePath` required the
same correction as `FileRootPath`.

Directory-valued policies now resolve the driver-expanded NT path through the
appropriate Win32 `GLOBALROOT` or DOS namespace and compare open filesystem
identities. The independent `QueryBoxPath` confinement check is retained; raw
readback alone is not accepted as proof of the effective policy. Scalar settings
remain literal comparisons. Mismatches report expected, expanded and raw values
with query flags; native query failures include section, key, index, flags and
NTSTATUS. A driver-free Windows regression obtains a real NT device path using
`GetFinalPathNameByHandleW(VOLUME_NAME_NT)`, proves it is textually different but
physically identical, and rejects a different directory. The targeted native
driver journeys still require rerunning after this correction.

The next provisioned run,
[40500527495](https://github.com/kleedaisuki/msync/actions/runs/40500527495),
made a separate configuration-serialization issue observable: an input
`\\?\D:\...` was stored unchanged but expanded to `\Device\Mup\?\D:\...`.
Sandboxie's DOS configuration parser treated the Win32 verbatim prefix as a UNC
server name. The physical-identity check correctly refused this invalid mapping;
it must never be made valid by stripping characters from the expanded result.

Directory settings now have a dedicated input serializer: canonical verbatim
drive paths become ordinary DOS drive paths, and verbatim UNC paths become
ordinary UNC paths before they reach `SbieDll_UpdateConf`. Unsupported device or
volume prefixes and verbatim components whose trailing-dot/space semantics would
change are refused. Runtime paths, argv and expected ownership identities retain
their original canonical representation. Unit regressions cover supported prefix
forms and ensure malformed MUP translations remain distinct, while the existing
NT-path identity and driver confinement checks remain mandatory. Native driver
journeys still require the next explicit gate; local unit success is not a native
isolation acceptance result.

The `e19a9b1` native gate then passed the foreign-rule rejection journey and
reached payload cwd validation in the remaining three. The fixture supplied an
ordinary DOS cwd while enrollment retained a canonical verbatim root; lexical
`starts_with` incorrectly rejected the same directory. The boxed worker repeated
the same lexical check, so normalizing only the parent check would not solve the
actual execution path.

Both ends now use one directory-containment routine: inspect raw and trimmed
ancestors for reparse points, canonicalize the paths, and compare directory
identities along cwd ancestry. This accepts real DOS/verbatim/case aliases without
loosening the boundary for prefix siblings, relative paths, regular files or
junction escapes. Driver-free regressions exercise both valid representations and
a real NTFS junction (including slash/dot spellings); another provisioned gate is
still required to validate the next payload-launch phase.

The deployment/source investigation and pinned installer are in
[transactions-v4.md](transactions-v4.md). The new runtime implementation is
`src/process/view_windows.rs` with the focused `windows_view/` native ABI, bounded
wire codec, logical-tree transport and boxed worker modules. It is not used by
the legacy v0.3.1 CLI execution path.

## Ownership and execution protocol

```text
parent: complete enrolled immutable roots
  -> fresh owned msync_<128-bit nonce> section and private FileRootPath
  -> driver-effective policy + actual file-root identity verification
  -> authenticated one-instance named pipe
  -> service-launched msync worker in exactly that box
  -> full binary tree seed through original logical paths
  -> native exact-argv payload and owned JobObject descendants
  -> concurrent stdin/stdout/stderr frames; true payload PID/exit receipt
  -> process/cohort quiescence
  -> logical tree export (including absence/deletion)
  -> immutable parent snapshots
  -> verified owned-section removal and private-directory cleanup
```

`Provider::probe` detects only an existing live installation. Explicit
`from_installation` and `MSYNC_SANDBOXIE_HOME` override conventional installation
paths; an injected worker also discovers its already-loaded service DLL. There
is no automatic dependency installation or native unisolated fallback.

`Provider::with_worker` requires an actual msync binary with the hidden
`WORKER_FLAG` dispatch. `prepare` is an **execution-time** operation: it creates
private files/configuration, so a compiler/check/plan path must not call it.
`PreparedView::effects` names a complete conservatively read/write managed
namespace, not just paths appearing in argv. Omitted mutable managed reads cannot
justify disjoint scheduling.

Only a fresh random section is configured. Existing sections and global settings
are never rewritten. Effective `OpenFilePath`, `OpenFilePathEx`, `NormalFilePath`
or `ReadFilePath` overrides make this first adapter refuse certification rather
than guess which rule wins. Required settings are read back through the driver;
`SbieApi_QueryBoxPath` is checked by physical root/ancestor identity. Policy is
revalidated immediately before launch. The worker also refuses seeding if the
host's live managed root is still visible.

The named pipe rejects remote clients and authenticates the kernel client PID
against the exact service-created worker handle and its Sandboxie membership.
The worker verifies the server PID and its own box. A nonce plus protocol-version
HELLO precedes resource data. The worker alone creates the native payload and
reports that `Child` PID: neither `Start.exe` nor the worker PID is mislabeled as
the program PID. A fast payload exit is not rejected by a later recycled-PID
membership lookup.

The source-verified reason for this bridge is important:
[`SbieDll_RunSandboxed`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/dll/callsvc.c)
does not forward standard handles through the service request. Redirecting
`Start.exe` does not give the target correct stdin/stdout/stderr. The boxed worker
opens its own native pipes and multiplexes them over the authenticated channel.

## Byte, path and lifecycle contracts

Control frames have a one-MiB hard bound checked before allocation. Native paths
and argv use length-checked UTF-16 rather than lossy JSON or shell serialization.
Relative tree entries reject root/prefix/parent traversal, NUL and alternate
streams. File data length, root order, complete export and PID/output-byte exit
receipts are checked. The original filenames remain visible through the private
namespace; no Sandboxie `drive/`, user-directory or tombstone layout is parsed.

Worker outputs immediately drain to buffer-bounded native spools, then to parent
spools. Combined stdout/stderr disk quota is optional and explicit; this is not a
process-RSS bound. Scalar pid/exit/meta values are replayable. Native error codes
and domain IO error kinds survive the binary failure protocol along with details.

Cancellation terminates the owned payload/cohort independently of stdin/control
backpressure, drains physical IO, and never exports a partial view. Export joins
the complete owned box cohort except the trusted exporter worker, then waits for
that worker and checks the cohort again before cleanup. PIDs are not treated as
completion evidence. No API ever uses `terminate_all` or a null box name.

Drop requests cancellation but retains unsealed private data/configuration for
diagnosis; explicit `discard`, `cancel`, or `seal` is the normal lifecycle.
Cleanup verifies root identity, removes only the owned section, reads back its
absence, and then deletes private files. Once section ownership is released, a
late destructor does not terminate a newly created section with the same name.
Cleanup failure is visible and is not evidence that user file data was committed.

This mediates registered filesystem resources. Default box virtualization also
contains unrelated filesystem/registry writes; AFD and network-file rules deny
external network file/socket access. It is not a universal rollback protocol for
external services or an independently established hostile-syscall security proof.

## Configuration API evidence

The pinned [`SbieIni DoUpdate`](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/apps/ini/update.c)
returns zero for many update failures except its explicit wrong-password case.
Its process exit status is therefore not a reliable successful-policy receipt.
The implementation calls typed `SbieDll_UpdateConf` and checks the status and
driver-effective readback instead. DLL BOOL, BOOLEAN, NTSTATUS, UTF-16 box-name
buffers and byte capacities follow the pinned headers, not guessed C layouts.

## Focused acceptance

Local gates: five protocol/policy/error tests, one deterministic unavailable-
installation test, and strict library/Windows-view target Clippy. These do not
exercise or certify the driver.

After signed installation in a disposable Windows runner:

```text
cargo test --locked --test process_view_windows_v4 -- --ignored --skip boxed_child_fixture --nocapture
```

The four real journeys cover frozen host-versus-private versions, absolute and
relative writes, deletion, descendants and true PID; binary stdin and 768-KiB
stderr with nonzero exit replay; cancellation without host publication; and a
foreign direct-access rule added only to the owned fixture section invalidating
the view before launch. Missing capability must fail this explicit gate, not
turn into a reported native pass.
