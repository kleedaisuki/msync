# Word32 native worker instruction probe

This isolated probe compiles the production x64 and AArch64 emitters and
executes their actual instruction bytes. Its C driver is a test-only
independent scalar oracle. There is no external assembler or C compiler
dependency in the product.

The tested worker slots hold zero-extended Word32, not boxed Numbers.
Shift counts are proven in 0..31 before selecting the worker. All
thirteen operations are checked over edge values, all 32 counts for
each edge shift/rotate input, and seeded varied words. There is a
separate unsigned Word32-to-binary64 conversion check. A counted
126-row mutating worker rotates each word exactly once across 41
resumes, checking every output and operation count. This covers the
x64 CL reservation together with each calling convention's row pointer,
fuel and resume registers.

On Linux x64, the driver checks the System V convention and uses
Clang's `ms_abi` caller to execute the Microsoft x64 bytes. This tests
the Windows argument-register assignment, not Windows process loading
or page publication. On Linux ARM64, the same driver checks AAPCS64,
including the NEON population-count sequence. On baseline x64, the
emitter does not require POPCNT, LZCNT, TZCNT or BMI.

From the repository root:

```sh
dotnet run --project tests/Performance/NativeJit/Word32WorkerProbe/probe.csproj \
    -c Release --artifacts-path .temp/native-word32/build
clang -O2 -std=c17 -Wall -Wextra -Werror \
    tests/Performance/NativeJit/Word32WorkerProbe/probe.c -o .temp/native-word32/probe-x64
.temp/native-word32/probe-x64
```

Compile the same driver with the Linux AArch64 sysroot and run the
result from the repository root on ARM64 or under `qemu-aarch64 -L <sysroot>`.
No runtime import, program-level throughput or platform page publication
claim is made by this instruction test.
