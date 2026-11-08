# Native copy metadata in 0.8

## The concrete mismatch

The initial C# runtime captured file times with `DateTime`/`DateTimeOffset`.
Those values have 100ns precision; Linux filesystem timestamps have separate
seconds and nanoseconds. The released-candidate image `ca5c2eb2...` copied a file
with mtime `1700000000123456789` to `1700000000123456700` without an error.
The earlier performance fixture used timestamps divisible by 100ns and could
not discriminate this loss. Correct timestamp preservation is independent of
any throughput optimization.

`NativeFileMetadata` now captures a node's kind, size, permission bits and raw
mtime together. Linux uses the stable 256-byte `statx` ABI, requesting TYPE,
MODE, MTIME and SIZE explicitly. The captured value owns no handles or cached
filesystem facts beyond that selected copy. `utimensat` installs the timestamp
on the owned stage before its single file flush and replacement; directory times
are restored after child publication. `AT_SYMLINK_NOFOLLOW` updates a staged link
itself, never its target. The existing libc-stat ABI supplies a compatibility
fallback when the statx operation is unavailable; there is no kernel-version
policy or persistent registry.

Windows' native timestamp representation is 100ns. Directory timestamps use the
Directory API rather than File APIs requiring an ordinary-file handle: the real
Windows runner exposed AccessDenied in the initial tree journey. Link timestamp
updates open the reparse entry itself with backup semantics. They do not change
an unrelated linked target or source hard-link metadata.

See the primary [`statx`](https://man7.org/linux/man-pages/man2/statx.2.html) and
[`utimensat`](https://man7.org/linux/man-pages/man2/utimensat.2.html) contracts.

## Archive composition without losing nanoseconds

`.NET 10`'s [PAX writer](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarHeader.Write.cs)
overwrites the local `mtime` extended attribute from its 100ns property. An
installed-runtime probe confirmed that merely supplying a raw `mtime` to the
PaxTarEntry constructor does not preserve it. No handwritten tar encoder or
additional local `tar` dependency is needed: the existing BCL writer composes a
standard global PAX timestamp followed by a GNU member. GNU members retain long
names and large-file support without emitting a local PAX mtime that overrides
the exact global value. GNU tar extracted `1700000000.123456789` exactly in the
initial discriminating probe.

Each uploaded node gets its own timestamp header. Downloads and selector
archives explicitly request native `tar --format=pax`; the previous default GNU
header retained only whole seconds. The BCL reader keeps the original PAX mtime
string, which is parsed as a decimal seconds/nanoseconds pair, not floating point
or a potentially overflowing total-nanoseconds integer. Negative fractions use
floor semantics, e.g. `-0.123456789` is seconds `-1`, nanoseconds `876543211`.
All of these metadata and data bytes remain on the transfer's one selected SSH
channel.

Remote directory installation captures its source timestamp **before** moving
children: reading the source directory time afterward observes the installer's
own removals, not the original input. The native helper reapplies that captured
nanosecond timestamp after publishing children. Actual upload/download checks
exposed and discriminate this sequencing defect.

## Reproducible user journeys

```
python3 tests/IO/nanosecond_metadata.py /absolute/path/to/msync --ssh \
  --output .temp/io-v080/nanosecond-source-object-ssh.json
python3 tests/IO/directory_metadata.py /absolute/path/to/msync
```

The nanosecond journey checks source, relocatable object and bound executable
execution. It checks regular files, directories, relative and dangling symlink
entries, executable mode, negative Unix times, unchanged source data/metadata,
and an unrelated source hard link. The `--ssh` extension uses real loopback
OpenSSH: three uploads and three downloads each have exactly one native client,
authentication and server exec session; compilation/binding have none. It checks
filesystem `st_mtime_ns`, not the already-rounded .NET property. The latest
accepted managed CLI result is retained under
`.temp/nanosecond-ssh/s-8qnajboz/nanosecond-result.json`; the maintained fixture is
the reproducible procedure, not a dependency on that disposable directory.

The directory journey uses representable 100ns timestamps and is portable to the
actual Windows runner, where it discriminates the directory-handle API repair.
