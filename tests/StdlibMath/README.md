# Number mathematics

Run `dotnet run --project tests/StdlibMath` from the repository root. The probe
checks IEEE binary64 details directly, then compiles source compositions at O0,
O2 and O3 through relocatable and executable binary round trips.

`$std::math` has Number/Bool operations rather than C++ integer overloads or a
floating-point environment. Elementary domain errors return IEEE NaN/infinity;
they do not set `errno` or throw. `round` resolves halfway cases away from zero;
`round_even` explicitly selects ties-to-even. `fmin`/`fmax` prefer the non-NaN
operand and order signed zeros; generic source `min`/`max` instead use the
language's normal ordering. `fmod` is the truncating quotient remainder;
`remainder` uses an integral nearest-even quotient. `fma` is one rounding, not a
rewrite of ordinary multiplication plus addition.

Native primitives use the platform's .NET `Math`/`Double` implementations.
Ordinary source functions compose powers, angle conversion, bounds predicates
and finite-endpoint interpolation. No expression reassociation or new numeric
type is introduced. Clamp's reversed bounds produce a source-owned error.

The `$std::bits` primitives `bit_and`, `bit_xor`, `shift_right`, and
`rotate_right` take exact unsigned 32-bit Numbers and return exact Numbers.
Shift and rotate counts are checked against 0..31 rather than silently
masked. The probe compares edge and generated cases with an independent
integer oracle, checks source-owned admission failures, and observes the
direct raw-stack path on both round-tripped artifact stages. Word32 zero
is positive zero; ordinary binary64 signed-zero behavior is unchanged.

## Installed CLI bit operations

Run `python3 tests/Msync.EndToEnd/bits_journey.py --binary /path/to/msync`
to exercise every `$std::bits` operation and nested calls. The deterministic
oracle models words with binary strings and exact integer arithmetic rather
than copying the host operators. The program reads words from stdin to
prevent constant folding.
The journey deletes the source before running the portable artifact, then
deletes the portable artifact before running the bound executable elsewhere.

Use `--profile` to compare actual VM native-call counts and fast-call counts
against the executed cases.

Run `python3 tests/Msync.EndToEnd/bits_native_worker_journey.py --binary
/path/to/msync` on a guest JIT-supported host to test the same source-free
artifact with the guest JIT enabled and disabled. It scores both paths with
an independent oracle, compares logical calls against the expected branch
counts, and requires real machine-worker entries only on the enabled path.
An invalid dynamic shift after warmup checks the guard's canonical fallback.

Relevant primary references:

- [C++ mathematical functions](https://eel.is/c++draft/c.math)
- [C++ linear interpolation contract](https://eel.is/c++draft/c.math.lerp)
- [.NET midpoint rounding](https://learn.microsoft.com/en-us/dotnet/api/system.math.round?view=net-10.0)
- [.NET scaled hypotenuse](https://learn.microsoft.com/en-us/dotnet/api/system.double.hypot?view=net-10.0)
- [.NET unsigned rotate right](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.bitoperations.rotateright?view=net-10.0)
