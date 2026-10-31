# msync's Rustyline external-printer correction

This is the published Rustyline **19.0.0** source package, retained under its
original MIT [LICENSE](LICENSE). Its upstream VCS identity is
`44c7025089debc46528fcc17b79d0e30cec0d60c` in
[kkawakam/rustyline](https://github.com/kkawakam/rustyline/tree/44c7025089debc46528fcc17b79d0e30cec0d60c).
The crates.io package archive SHA-256 is
`56e1d6bd81c2d5feafd2419138df9183c45a10a492c4e873b8b0acb8eb2b549e`.
Cargo cache markers are not part of this maintained dependency.
The retained `Cargo.lock` pins standalone verification's development dependencies;
the application's production dependency graph remains pinned by the root lockfile.

## Why this patch exists

The upstream external printer uses a blocking single-slot message channel while
the line reader can concurrently return. A REPL completion can therefore deadlock
after the reader leaves raw mode but before the producer's send completes. The
real msync Ctrl-C/drain/capture journey reproduced this with both final output
files already published: it was a notification ownership cycle, not child exit,
network latency, or incomplete payload I/O.

Only the production source files `src/tty/unix.rs` and `src/tty/windows.rs` are
changed from the package:

- Use the standard unbounded message channel so a departed reader cannot block a
  producer. msync additionally coalesces each application notice burst.
- Unix wake descriptors are nonblocking. A saturated wake pipe already contains
  a wake; message ownership stays in the channel. Drain accumulated wake bytes
  before collecting the queued messages, preserving message order and contents.
- Windows resets the event before collecting queued messages, and treats a stale
  event as an empty nonblocking read. Multiple coalesced sends must not strand a
  message or turn into another blocking receive.
- Keep the established terminal input, rendering, native pipe/event mechanisms,
  raw-mode fallback and public API. No notification thread or replacement editor
  is introduced.

Raw-mode messages retain the renderer's implicit newline per message before
coalescing; non-raw direct writes keep their original byte behavior.

The dependency's `external_printer_` unit tests exercise a departed reader,
saturated Unix wake pipe, complete ordered delivery and stale wakes. Windows
has the equivalent queue/event case; native execution is platform-specific.
msync's actual PTY and idle-completion journeys remain the application-level
acceptance, not substitutes for those deterministic transport cases.

Run the focused dependency tests separately (this package is not a root workspace
member):

```sh
CARGO_TARGET_DIR="$PWD/target" cargo test --locked --manifest-path vendor/rustyline/Cargo.toml \
  --no-default-features --features custom-bindings,buffer-redux --lib external_printer_
cargo test --locked --test barrier_terminal_v5 --test latency_v6
```

When updating Rustyline, compare this narrow correction against upstream and
remove the override only after these cases and the real PTY journey pass. Do not
edit unrelated vendored behavior or hide this issue with test sleeps/timeouts.
