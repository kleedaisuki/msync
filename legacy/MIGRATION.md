# Preserved Rust implementation (0.7.3)

This directory retains the complete Rust implementation, embedded standard
library, fixtures, vendored terminal editor, and its original build/package
helpers. The 0.8 implementation lives under the repository's `src/Msync.*`
directories and does not depend on this implementation at runtime.

Build or run the original CLI from the repository root:

```sh
cargo build --locked --manifest-path legacy/Cargo.toml --release
cargo run --locked --manifest-path legacy/Cargo.toml -- run old-script.msync
```

The old language and serialized object formats keep their original meaning.
Do not reinterpret them as 0.8 source or bytecode. Existing published 0.7
packages and tags are unchanged. `README.md` is the original 0.7 reference;
the current design history remains under the repository-root `docs/design`.

The original package helpers are retained for reference and require the same
original checkout assumptions; the current root packaging targets Native AOT.
The Rust source and embedded fixtures remain independently buildable through
`Cargo.toml` without the C# toolchain.
