# SSH modification-time fidelity in v0.6.5

## Defect and existing primitives

The real OpenSSH fixture found that frozen `d501fd2` upload changes
`1700000000123456000` ns into `1700000000000000000` ns. SCP/SFTP `-p` carries
whole-second modification times in this path. Download additionally requested
GNU find `%Ts` and parsed only an integer receipt, so its receipt could not repair
fractional precision either. The performance fixture's whole-second controls
remain separate from this fidelity regression.

The repair keeps OpenSSH SCP for bytes and the existing publication/recovery
owners. Upload reapplies only fractional modification times to private remote
staging before the existing copy/publication operation, using GNU `touch --date`.
Metadata arguments are bounded batches; source traversal is postorder so directory
times are applied after children. A whole-second source causes no additional SSH
metadata command. POSIX printf-octal relative paths retain literal filename bytes,
including trailing newlines and non-UTF-8 Unix names. The helper refuses escaping
paths, symbolic-link ancestors and non-finite nodes, and uses no-create/no-follow
touch flags. It never modifies the public destination or grants cleanup authority.
Unix mode preservation and Windows-origin portable permission policy are unchanged.

Download now asks GNU find `%T@`. The checked parser accepts old integer receipts
and exact nanoseconds, including GNU find's extra trailing zero. Its negative
encoding is important: `%T@` for epoch minus half a second is `-1.5000000000`,
meaning floor seconds `-1` **plus** positive `500000000` ns. This is not the same
format as GNU touch's mathematical `@-0.500000000`. Floating-point conversion,
silent rounding and unsupported precision are avoided.

## Focused verification

The targeted `source_metadata_` Rust selection passes six tests, including a
96-case property over signed floor seconds and nanoseconds. Existing receipt
framing, path/duplicate rejection, readonly metadata and no-follow tests remain.
The `upload_` selection checks signed touch formatting and executes the actual
GNU helper against readonly payloads, newline/non-UTF-8 filenames and an escaping
relative path. Real authenticated upload/download checks use the existing fixture:

```sh
python3 benches/ssh_v065.py /absolute/path/to/msync .temp/v065/ssh-fractional.json \
  --samples 1 --case upload --case download --mtime-ns 1700000000123456000
python3 benches/ssh_v065.py /absolute/path/to/msync .temp/v065/ssh-preepoch.json \
  --samples 1 --case upload --case download --mtime-ns=-500000000
```

The optional `--mtime-ns` changes only the fixture assertion value, not the default
whole-second performance cohort or its AB/BA method. These are fidelity journeys,
not timing measurements while builds are active.

## Primary references

- Installed GNU findutils 4.10.0 manual (`find(1)`, `%T@`) and
  [GNU find time formats](https://www.gnu.org/software/findutils/manual/html_node/find_html/Time-Formats.html).
- Installed GNU coreutils 9.7 manual (`touch(1)`, `--date`, `--no-create`,
  `--no-dereference`) and [GNU touch](https://www.gnu.org/software/coreutils/manual/html_node/touch-invocation.html).
- Existing source receipt and portable-permission decisions in
  [remote publication](remote-publication-v5.md); the actual authenticated fixture
  is documented [separately](ssh-performance-v065-fixture.md).

## Accepted local evidence

The local focused Rust selections passed six `source_metadata_` cases (including
96 generated timestamp cases) and four `upload_` cases (two existing source-capture
checks and two new timestamp checks). After consolidating capture into the
existing validation traversal, the actual GNU-helper test passed again and two
real authenticated uploads preserved the exact positive and pre-epoch fractional
values. The earlier unchanged download portion passed both corresponding real
checks. No complete suite or native Windows run is implied.

The first four upload/download checks used the frozen debug image SHA-256
`f13ffe68e1cbfd3f565c0b74129ded6affaa15bca84d3024d0c15abd584c942c`.
The post-consolidation upload checks used
`a7ec7c7c0e6f15b2a1e8843ce01e8b5736980647a4b948f6f29738aab9c1d8e0`.
Those identified images predate the final locale-only `LC_ALL=C` export, whose
shell bodies were syntax-checked separately; the integrated SSH owner will build
its final identified image. The exports keep numeric helper fields independent
of the remote locale and do not alter file permissions or payload transport.
Raw functional logs/JSON are under `.temp/v065/ssh-{fractional,preepoch}-metadata*`
and `ssh-{fractional,preepoch}-upload-final*`; the useful outcomes above survive
cleanup. The elapsed values in these diagnostic runs are not performance claims.

The frozen integrated optimization image SHA-256
`e18d9f36b905630b9915cc5634e4ff4833a66ee34924650fc555913482433f7f`
subsequently passed all four exact positive/pre-epoch upload/download checks,
including the final locale handling. Raw reports are
`.temp/v065/ssh-final-positive-metadata.json` and
`.temp/v065/ssh-final-preepoch-metadata.json`. These final-image functional checks
ran alongside other functional gates, not in a performance timing window.

Fractional upload timestamps are captured while the existing finite-source
validation already owns each metadata observation, before SCP starts. A receipt
contains only entries needing repair and lives for one upload; there is no second
local tree scan or global filesystem cache. The remote helper checks the private
finite tree once per metadata batch, not with a `dirname` subprocess per entry.

## Native SystemTime fixture precision

The first native Windows library run exposed a test-construction assumption,
not lost transfer precision: `UNIX_EPOCH + 123456789ns` already becomes
`123456700ns` in Windows `SystemTime`, before the formatter receives it.
[Rust's platform-specific SystemTime contract](https://doc.rust-lang.org/std/time/struct.SystemTime.html#platform-specific-behavior)
documents 100ns intervals on Windows and nanosecond intervals on Linux. The
shared formatting fixture now uses exactly representable `123456700ns`; Unix
keeps an additional exact `123456789ns` assertion. Both retain the signed
pre-epoch cases. Production timestamp formatting/repair code is unchanged.

After the independent Windows logical-root normalization repair, the refreshed
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`
delivery image also passed all four exact positive/pre-epoch upload/download
checks. Reports `.temp/v065/ssh-delivery-positive-metadata.json` and
`.temp/v065/ssh-delivery-preepoch-metadata.json` retain their own image/probe
identities; these do not relabel the earlier `e18d9f36` evidence.
