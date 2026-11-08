# WSL archive producer lifetime (0.8)

## Actual boundary isolated

Windows Native AOT acceptance against an owned WSL1 distribution exposed a
finite-download EOF. The source files existed and independent Python captures
had correct bytes. The C# phase probe then separated raw pipe capture, live
TarReader, buffered parsing and a generic native producer. A 64KiB BufferedStream
made the 10KiB small archive pass but still truncated the larger payload. It was
not selected as a fix, and the product adds no read-ahead payload buffer.

The decisive payload has 1,280,123 bytes (all byte values, including NUL, FF and
CR/LF) and SHA-256
`7a16c6063e0e23004a13995dd6dc3c462a605f53afd2835e5e9b789549c349d2`.
Actual run `42477767279` compared the same producer with and without a receiver
receipt. The old unbuffered small reader received 512 bytes then EOF; large
unacknowledged captures/reads also lost data. Keeping the guest alive until the
receiver had consumed logical tar end records preserved the full 1,290,240-byte
large archive, its exact payload/hash, native exit 0 and offline validity. This
worked **without** buffering, and the generic native self-producer passed all
modes on the valid archive. The evidence isolates the WSL guest-lifetime/relay
boundary, not a general .NET parser limitation or a filename policy.

Raw proof:
`artifacts/ci/42477767279/windows-wsl/native-win-x64-fc1d5ae163f54954bf5da74f7bca49fd7b210f9c/`.
The maintained discriminator is `tests/IO/WslTarProbe`; the fixture owns and
unregisters only its imported distribution.

## Selected protocol

Only a Windows-client WSL archive producer uses the transient receipt:

```
owned guest shell:  run tar in a child -> wait for receiver receipt -> exit
owned receiver:     TarReader consumes logical archive end records
                    -> send explicit LF ACK -> close owned stdin
                    -> drain remaining record padding -> wait for native exit
```

The parent guest shell has `set -eu`, so a failed tar producer does not wait for
a receipt. The receiver never inherits foreground input. Error/cancellation
retires its owned native process and pipe; no persistent journal, recovery scan,
transactional rollback, timeout sleep, file quota, spool or extra client is added.
SSH keeps its accepted raw archive path.

Direct downloads and selected ranges share this ownership. Relays use the BCL
TarReader over a forwarding stream to discover logical archive end while
forwarding the same bytes. They then acknowledge and forward trailing padding;
waiting for physical EOF before ACK would be cyclic. The forwarding adapter uses
the existing copy buffer and does not implement a second tar parser.

## Truncated-member publication

The same actual large-file probe showed that a BCL entry DataStream may return
ordinary EOF after fewer bytes than its declared entry length, with failure only
on a later padding/header read. Publishing that short member first would violate
ordinary-file atomicity. The archive sink compares its owned stage position with
the declared member length **before** metadata, fsync and replacement. This is a
protocol length invariant, not a size limit or duplicate source inference.

`tests/IO/archive_short_body.py` injects a completed member followed by a short
member and verifies: the old target remains, the completed member stays in place,
the owned stage is removed, and diagnostics retain entry path plus declared and
received byte counts. `tests/IO/ArchiveReceiptContracts` exercises the linked
production receipt/forwarding helper with large and empty archives. Actual final
Windows source/object/executable WSL acceptance validates the product integration;
these focused probes do not replace it.
