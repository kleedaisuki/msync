# Record-valued callable contracts

This focused executable covers a nullable-admission/flow-type distinction in
returned closures. `Result::err(error?)` may accept Null, but a constructor call
with a record captures a non-null record value. If that parameter is never
assigned, later callback specialization must not fabricate an additional Null
alternative merely because the parameter admitted it.

The positive case folds a success/error record and then uses `Result::get_or`
with a second result. The second call revisits the earlier eliminator contract,
which exposed the erroneous widening. All record fields and both variant
callbacks are exercised through source, serialized relocatable bytecode and
serialized executable bytecode at O0/O2/O3.

A second constructor really does assign its captured parameter through a
returned `clear` operation. Its reader must continue to return Null after that
write; the read-only analysis must not be inferred from the selected call alone.
Negative cases retain incompatible-record and nullable-result rejection. The
diagnostic assertions require the latter to display `Scope { ... }?` rather
than print indistinguishable expected/actual records.

The suite passed 63 checks at O0/O2/O3, covering all three execution stages.
The exact command-policy source also checks at each optimization level without
executing its children.

```sh
dotnet run --project tests/CallableResultContracts \
  --artifacts-path .temp/callable-result-contracts
```

The same executable accepts `--check /absolute/source.msync` to check a larger
command-policy program at O0/O2/O3 without starting its child processes. Live
Process ownership and actual command execution belong to the command-policy
journey, not this compiler regression.
