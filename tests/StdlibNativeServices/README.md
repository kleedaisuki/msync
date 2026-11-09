# Native source service families

The focused journey executes the public named Scope families, not a second
service implementation. Instances are ordinary data records. Associated
operations take an explicit receiver and specialize with its selected policy.

- `io::File` and `io::Directory`: actual UTF-8 write, append, read,
  transfer and rename; shared aliases, fresh records, fixed Path storage,
  directory snapshots and shared default policy identity.
- A checked, source-defined in-memory `TextIOPolicy`: construction and signature
  checking execute no callbacks; read/write/append and line enumeration select
  only that policy and do not touch the physical path. The default line producer
  retains native streaming rather than buffering the whole file first.
- `process::Command` and `process::Completed`: actual owned child stdin,
  Unicode captured output, reusable argument snapshots, fresh configurations,
  and publication of completed output. Process identity and native control remain
  in the existing Process value.
- Wrong policy signatures, non-Path assignment and malformed process
  specifications fail at compile time with source-owned diagnostics.

The same programs run at O0/O2/O3 through source, serialized relocatable
bytecode and serialized context-bound executable bytecode. The canonical File
and completed-result records are checked to contain no per-instance method
closures. Existing Pascal-case callable adapters retain their previous contract.

```sh
dotnet run --project tests/StdlibNativeServices -c Release \
  --artifacts-path .temp/native-services-build
```

Disposable fixtures stay under repository `.temp` and are removed by their live
owner. Coordinate product builds with other implementation owners, or build the
project in an isolated artifacts directory as above.

The focused managed build completed with zero warnings/errors and passed 180
assertions (2026-11-09). This is component/source integration evidence; final
Native AOT Windows/Linux acceptance belongs to the delivery journey.
