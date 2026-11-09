# WSL native stdout / tar phase probe

This bounded diagnostic uses actual C# Process.Start and the same WSL argument,
redirected-input, redirected-output and closed-input ownership as the product.
It is not a replacement transport and changes no product behavior.

```
dotnet publish tests/IO/WslTarProbe/WslTarProbe.csproj -c Release -r win-x64 \
  --artifacts-path artifacts/wsl-tar-probe-build -o artifacts/wsl-tar-probe
artifacts/wsl-tar-probe/WslTarProbe.exe --distribution OWNED_DISTRO \
  --source '~/.codex/config.toml' --work-directory ORIGINAL_WINDOWS_CWD \
  --output-dir .temp/wsl-diagnostics/home
```

Run before the owned fixture distro is unregistered, with the original Windows
environment. `capture` drains the native stdout pipe into raw bytes, then replays
through a nonseekable wrapper. `live` feeds that pipe directly to TarReader,
copies each entry to the same asynchronous FileStream shape as the product,
flushes, requests subsequent entries, and drains the remaining archive records.
A tee saves all actual bytes and requested/returned read sizes with offsets.
`buffered` inserts the BCL BufferedStream with a 64KiB read-ahead buffer above
that tee, keeping actual native reads observable. The same three modes also run
a generic native self-producer that emits the captured archive byte-for-byte,
separating WSL relay behavior from general Windows Process/stream behavior.
The JSON records native exit, archive length/SHA, entry length/SHA, exceptions
with full stacks, and offline replay outcome. Native stderr remains inherited
like the product; the fixture owner captures the probe's stderr.

The owned control-program literal is normalized to LF before argv data is bound.
The probe does not normalize filenames, payloads or assembled commands. A Linux
shell-backed launcher smoke establishes only the probe's phase/logging logic;
only execution against actual Windows WSL establishes the boundary in question.

`wsl-ack-live` and `wsl-ack-buffered` test producer lifetime independently of read
buffer size. The guest runs tar without exec and waits on its owned stdin for a
receiver receipt. After TarReader sees the archive end records the receiver sends
ACK, closes stdin, drains remaining archive padding and waits for native exit.
Failure sends REJECT, retaining the original parser exception. No sleep, file
size quota, persistent state or additional native channel is used. The generic
self-producer uses this acknowledged archive when available, so a truncated
unacknowledged WSL capture does not invalidate its large-file control input.
