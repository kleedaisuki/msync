# Native Windows directory-tree I/O

The Linux measurements in [I/O attribution](0.9.2-io-attribution.md) do not
predict Windows-local behavior. The Windows build already stages via
`CopyFileExW`, using a retained native handle for one explicit
`FlushFileBuffers` and then a per-file atomic replacement. For upload,
the release and candidate run Windows-native OpenSSH but the archive
receiver and its publishing commands run on Linux.

## Work and measurement

[The Windows harness](../../tests/Performance/windows_tree.py) reuses the
source-tree and artifact-batch distributions: standard seed 17, 377 files
and 5.53 MiB versus 103 files and 45.75 MiB on first deploy. It uses a fresh
source and target for each image, then revises and redeploys the same whole
tree into its now populated target. A receiver-only marker must survive.
First deploy and redeploy are separate measurements, not change detection.
The file set, exact bytes, file times and empty directory are checked;
NTFS mode is checked on the local lane, not compared to POSIX permissions.
Receiver hashing and fixture setup are outside the command timer.

Actual Windows Server 2025 x64 Native AOT; official 0.9.2 Windows release
zip versus a same-host candidate built with SDK 10.0.403. One warmup and
seven alternating pairs per image, profile and lane, telemetry off. One
independent diagnostics observation per image/cell: never compare these
absolute times to the uninstrumented samples. The new Windows native copy
and flush spans distinguish the two actual stages, carry errors, and
preserve the existing retained-handle policy.

```powershell
python tests/Performance/windows_tree.py --binary CANDIDATE --baseline RELEASE --work .temp/windows-tree
python tests/Performance/windows_tree.py --binary CANDIDATE --baseline RELEASE --work .temp/windows-peer --remote-only --ssh-config CONFIG --remote-root /owned/scratch
```

The external configuration pins the receiver's host key and supplies
the `Perf_Fixture` alias. The harness owns one uniquely named child per
round and does not clear the caller's scratch parent. The population
design is informed by the [FAST filesystem metadata study](https://www.usenix.org/conference/fast-07/five-year-study-file-system-metadata);
none of these distributions is a claim about Windows user frequency.

## Local attribution before enabling a queue

[Windows run 44708661240](https://github.com/kleedaisuki/msync/actions/runs/44708661240)
verified 56 timed NTFS deployments with no native Windows queue in either
image. Values are CLI wall time P50; in parentheses, the median relative
change within each pair.

| Profile | First deploy (ms) | Redeploy (ms) |
| --- | ---: | ---: |
| Source tree | 336 → 345 (+1.3%) | 350 → 359 (+2.7%) |
| Artifact batch | 140 → 140 (+0.6%) | 145 → 144 (+0.9%) |

The instrumented candidate spent 1,139/1,050 ms in 377/378
`FlushFileBuffers` calls against transfer spans of 1,490/1,297 ms,
respectively (76%/81%). On that same first-deploy trace the native copy
span union was 248 ms and publication 60 ms. The tiny telemetry-off
changes above are not a production speedup; the separate trace is
mechanism evidence for keeping each flush while overlapping independent
work. Microsoft's [native copy documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexw)
and [multithreaded tree-copy tooling](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)
motivate testing concurrency without substituting a byte-only stream copy
for the native Windows entry semantics.

The same runner imported the disposable WSL1 distro but did not find
`apk` on the direct executable search path; no WSL performance observation
was accepted. In this configuration WSL would still be loopback, not an
external Windows-to-Linux measurement.

## Rejected local NTFS experiment: four staged native copy workers

[Windows run 44709587892](https://github.com/kleedaisuki/msync/actions/runs/44709587892)
tested up to four independent ≤64 KiB file stages using the existing
`CopyFileExW` and explicit flush, with strictly ordered replacement. Fresh
and merged directory trees passed the standard oracle. A real blocked
rename also preserved the published prefix, NTFS alternate data streams,
readonly state, timestamps and receiver-owned data, and removed the
abandoned stages (including readonly ones).

| Profile | First deploy (ms) | Redeploy (ms) |
| --- | ---: | ---: |
| Source tree | 226 → 467 (+105.2%; 0/7 wins) | 224 → 463 (+79.1%; 0/7) |
| Artifact batch | 243 → 320 (+33.7%; 0/7) | 243 → 382 (+50.1%; 1/7) |

Each pair uses the same machine. Do not compare the absolute baselines of
the two different hosted runners. The prototype was removed from the
working implementation; its reproducible revision is
`bcff22d0868aa2711774070f544aa894b55b1190`. Keep Windows' serial native
publication and the telemetry split. In the source-tree first-deploy
trace, time inside publish grew from 36 to 297 ms with the queue; this
shows that even non-flush calls got slower while staging overlapped.
The trace cannot attribute that difference to NTFS locking, storage flush
serialization, antivirus filters, the VM backing disk or the managed
thread pool. The next discriminating test would use Windows Event Tracing
(ETW) with the telemetry writer on a distinct volume, not tune queue
depth based only on this result. Microsoft's
[`FlushFileBuffers` documentation](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)
warns about the cost of frequent flushes; it does not establish that
concurrent flushes are the right remedy for this contract.

## Windows OpenSSH to a separate Linux host

[Run 44709081341](https://github.com/kleedaisuki/msync/actions/runs/44709081341)
completed the external lane: Windows Server 2025 native OpenSSH to an
isolated Debian Linux receiver on a different host, with overlayfs storage,
GNU coreutils 9.7 and a pinned SSH host key. This is not WSL, loopback,
Windows-to-Windows SSH, or SMB. The run's WSL setup step failed separately;
its NTFS and two-host steps and artifact upload passed. The scratch
receiver and its credentials were retired after the experiment.

| Profile | First deploy (ms) | Redeploy (ms) |
| --- | ---: | ---: |
| Source tree | 4,618 → 2,678 (−42.0%; 7/7 wins) | 4,690 → 3,706 (−20.4%; 7/7) |
| Artifact batch | 8,092 → 7,554 (−5.3%; 5/7) | 8,028 → 7,863 (−1.6%; 4/7) |

All 56 timed external commands passed the receiver hash/timestamp/file-set
oracle. In separate source-tree first-deploy diagnostics, the inclusive
archive-write span was 2,706 ms in both images, while time from the end of
that span to transfer completion fell from 2,042 to 109 ms. The candidate
reported one subtree rename, zero leaf moves, and zero recursive directory
installs. On redeploy it reported 378 leaf moves across 48 directories and
the post-archive gap fell from 2,151 to 1,126 ms. The larger batch still
spends most of its elapsed time in streaming (including pipe backpressure);
the smaller paired changes are not evidence of faster networking.

The second run's NTFS lane independently reproduced no meaningful local
change from the SSH/telemetry-only candidate. Remote-to-Windows download
and Windows SMB remain unmeasured here.

Local raw receipts: `artifacts/benchmarks/windows-tree-092/`.
