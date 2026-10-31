# Real OpenSSH performance fixture

`benches/ssh_v065.py` uses the installed OpenSSH client and an unprivileged
`/usr/sbin/sshd` bound to a fresh loopback port. The fixture lives under project
`.temp/v065`; no existing account, SSH configuration, known-hosts file or service
is changed. Generated host/client Ed25519 keys, an absolute authorized-keys file,
`StrictModes yes`, strict pinned host-key checking, public-key-only authentication,
disabled forwarding and disabled TTYs make this a real isolated SSH protocol
session, not a fake subprocess response. The current account executes only paths
within the fixture. The fixture shuts down its owned server on exit and preserves
logs for diagnosis.

This environment has OpenSSH 10.0p2 / OpenSSL 3.5.7 and an installed server.
Docker is unnecessary: the user is not in the socket's docker group, and sudo
requires a password. The fixture requires an existing suitable `/run/sshd` and
does not create or modify it. Dependency installation, if needed on another
host, is a separate explicit setup step.

## Measurement contract

The harness supports a frozen `before` executable and an optional `--after`
executable. Samples alternate AB/BA order. Each operation has separate local and
remote paths and a bounded process-group lifetime. Both executable hashes and
the probe-source hash must remain unchanged through the measurement window.
Remote paths are actually reached through OpenSSH/SFTP; direct filesystem reads
independently verify the resulting bytes, mode and mtime on this loopback host.

Cases are one 4 KiB upload, one download, an existence condition, a five-element
regex download, five independent uploads, and a failing missing-source download
that must retain the previous local destination and a located diagnostic. The
default mtime is deliberately an exact whole second. An initial stricter probe
found that the frozen baseline's upload changes `1700000000123456000` ns into
`1700000000000000000` ns; fractional upload fidelity is a separate historical
finding, not silently counted as passing metadata coverage here.

Small PATH wrappers record only each `ssh`/`scp` invocation and then `exec` the
installed client without changing arguments or results. Server `VERBOSE` logs
count accepted public-key authentications and started remote sessions. Thus a
control-socket check is a client invocation but not a new authentication, and a
multiplexed SFTP session remains a remote session. Compact fixture paths avoid
artificially exceeding the existing 95-byte owned-control-socket budget.
Per-command user/system CPU uses `getrusage(RUSAGE_CHILDREN)` deltas after the
joined msync command. It covers the local msync/client process tree, not the
still-running server daemon; cumulative peak RSS is deliberately not differenced
or mislabeled as a per-command measurement.

Example, during an exclusive timing window:

```sh
python3 benches/ssh_v065.py \
  target/performance-v065/baselines/msync-d501fd2-before \
  target/performance-v065/ssh-paired.json \
  --after target/performance-v065/baselines/msync-after --samples 7
```

Functional smoke verified all six cases. One baseline upload used seven `ssh`
clients and one `scp` client, with eight authenticated connections; five uploads
used 35 plus five clients and 40 authentications. Download used three connections,
the existence condition one, and the five-file regex case sixteen. These initial
smoke timings ran without exclusive CPU admission and are **not** accepted
performance comparisons. The parent SSH investigation owns paired results and
the causal engineering conclusion.
