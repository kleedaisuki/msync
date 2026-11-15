# Native backend representation journeys

Run `dotnet run --project tests/BackendLowering -c Release`.
The source compiler and binder produce an executable, round-trip its compact
codec, then execute it with native compilation off and forced. Numeric oracles
cover integer loop narrowing, fraction boundaries, exact-integer limits,
negative remainders, signed zero, NaN, captured writes and mutable scope aliases.
The integer journey requires actual unsigned linked and native operations;
connection tests exercise descriptor interning and native SSH/WSL dispatch.

`--inspect FILE.mexe` prints the serialized physical instructions and numeric
forms without executing the program.
