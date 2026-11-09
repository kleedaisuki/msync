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

Relevant primary references:

- [C++ mathematical functions](https://eel.is/c++draft/c.math)
- [C++ linear interpolation contract](https://eel.is/c++draft/c.math.lerp)
- [.NET midpoint rounding](https://learn.microsoft.com/en-us/dotnet/api/system.math.round?view=net-10.0)
- [.NET scaled hypotenuse](https://learn.microsoft.com/en-us/dotnet/api/system.double.hypot?view=net-10.0)
