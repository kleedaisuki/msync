# Linux ARM64 guest JIT

Linux ARM64 uses the existing typed numeric execution-region importer and a
direct AArch64 emitter. The format-5 object, source compiler, binder,
continuation PC, and interpreter fallback are architecture-independent.
The shipped x86-64 emitters are unchanged. Windows ARM64 and macOS are not
enabled by this change.

## Leaf ABI and lowering

The native entry is `int run(double *registers, int fuel, uint64_t *counts,
int resume)`. AAPCS64 assigns the inputs x0/w1/x2/w3 and the result w0.
The region uses d0/d1 as scratch and caller-save v16–v31 for frequently
used numeric slots. No guest-managed object pointers or host callbacks
cross the native boundary. Fuel, canonical exit and native resume indexes
are the existing tier contract; this also applies to continuation in an
inner loop and to regions with nonempty live operand stacks.

Scalar add, subtract, multiply, divide and negate use separate binary64
instructions. There is no fused multiply-add or new lowering for floating
remainder. FCMP branches use MI/LS for less/less-or-equal, GT/GE for
greater/greater-or-equal, and EQ/NE for equality, matching the canonical
unordered-NaN behavior. The counted emitter's capacity of 4,096 operation
slots comes from the scaled imm12 offset in LDR/STR, not a bytecode-format
limit. See [AAPCS64](https://github.com/ARM-software/abi-aa/blob/main/aapcs64/aapcs64.rst)
for the platform calling convention.

## Code-page publication

Linux ARM64 uses the system `libgcc_s.so.1::__clear_cache` helper to clean
and invalidate the newly generated range. It then seals the page RX and
uses Linux's private expedited sync-core membarrier before publishing the
entry pointer. The compiler thread's local instruction synchronization
alone would not order another thread's front end. An unavailable cache
helper, registration, mapping or publication leaves the interpreter
available. Pages are writable while populated, not writable and
executable at the same time. The page's lifetime is still owned by the
SafeHandle during execution.

The helper and kernel registration are initialized only on Linux ARM64.
`MSYNC_JIT=off` short-circuits before this platform setup. See Arm's
[cache helper explanation](https://developer.arm.com/community/arm-community-blogs/b/architectures-and-processors-blog/posts/caches-self-modifying-code-implementing-clear-cache),
[cross-thread note](https://developer.arm.com/community/arm-community-blogs/b/architectures-and-processors-blog/posts/caches-self-modifying-code-working-with-threads),
and [Linux membarrier](https://man7.org/linux/man-pages/man2/membarrier.2.html).

## Validation and release boundary

Use the [NativeJit journeys](../../tests/Performance/NativeJit/README.md)
with an actual Linux ARM64 CLI; use `--expect-fallback` on a host where
the kernel or optional helper does not admit native publication.

On Linux ARM64, run `python3 packaging/build.py --smoke` to build and
check the local Native AOT archive. The default host selection is
`linux-arm64`; the archive name is `msync-<version>-linux-aarch64.tar.gz`.
The public release matrix is unchanged. Keep the archive's `bin/`
together, including its ARM64 SQLite library, and use a GNU/Linux system
compatible with the build host's system libraries.

On Linux x86-64, use a [Native AOT cross toolchain](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
and publish the locked ARM64 payload into `artifacts/publish/linux-arm64`
before packaging it. For example, with `$AARCH64_CLANG` pointing to a
configured Clang cross-linker wrapper and `$AARCH64_SYSROOT` to the root
containing the ARM64 dynamic loader:

```sh
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release \
  -p:RuntimeIdentifier=linux-arm64 --self-contained true \
  -p:RestoreLockedMode=true -p:CppLinker="$AARCH64_CLANG" \
  -p:LinkerFlavor=lld -o artifacts/publish/linux-arm64
QEMU_LD_PREFIX="$AARCH64_SYSROOT" python3 packaging/build.py \
  --rid linux-arm64 --skip-build --runner qemu-aarch64 --smoke
```

The runner is used for both the staged executable's version check and
the smoke journey. It is not part of the archive or a dependency when
running the package on ARM64. See the [extracted package receipt](../../artifacts/benchmarks/linux-arm64-direct-jit-v086/package/README.md)
for the manifest audit, real file copy, SQLite telemetry and JIT replay
through the installed command symlink.

The [direct-emitter feasibility receipt](../../artifacts/benchmarks/linux-arm64-direct-jit-v086/README.md)
records an ARM64 Native AOT CLI running under QEMU user-mode: cold/hot
recurrence, nested rejoin, nonempty carry, IEEE conditions, remainder
fallback, post-effect tails and guarded Cell transitions. The receipt
also covers source and freshly bound execution and the standard signal
object with raw arguments and native read-only Cell tails. Its guest
syscall trace shows writable → executable publication and sync-core;
QEMU does not verify physical ARM64 cache coherence or performance.

The final production ARM/fusion freeze was independently cross-published
as Linux ARM64 Native AOT and run under QEMU user-mode. Source and freshly
bound member-tail both returned their independent expected values,
recording 74 native tails and the same 3,476 logical instructions as
JIT-off. The standalone Linux-produced signal object retained its
corpus output, 196,608 raw arguments, 196,611 split frames, 196,482
native tails and 9,984,844 logical instructions. See the
[exact production receipt](../../artifacts/benchmarks/linux-arm64-direct-jit-v086/production/joint-proof.json)
and adjacent build/source manifests. A linux-x64 managed compatibility
build from those same source inputs also passed.
The final guest trace also confirms the 140-byte writable→RX code page
and sync-core publication. Explicit opt-out has no guest JIT commands
64 or 32; Native AOT's unrelated GC membarrier commands 0/8/16 are
outside this publication contract.

The subsequent [private Number local receipt](../../artifacts/benchmarks/linux-arm64-direct-jit-v086/typed-local/README.md)
checks the O2 source, standalone x86-64-produced format-5 object, and
ARM64-bound object on the exact later Native AOT freeze. Each route
records three numeric-region local writebacks and 25 raw local writes;
auto enters native code once while opt-out keeps the same logical counts.
A division-based signed-zero control observes the correct sign at both
parities, including the unchanged-bits case that skips a writeback.
