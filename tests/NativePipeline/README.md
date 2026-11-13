# Direct native pipeline

`dotnet run --project tests/NativePipeline -c Release` analyzes real source,
round-trips standalone HIR, lowers both producers at O0/O2 through the public
compiler API, validates the native IR and round-trips native artifacts. Source and standalone
HIR must produce byte-identical native artifacts; non-session constants cannot
retain ephemeral source-index occurrence identities. Session path occurrences
are tested separately through persistent definition-context journeys. Mixed
String/Path/Connection fields, aliases, captures, finite layouts, loops,
recursion and returned closures do not use the old bytecode importer.

Compiler observation checks compare real lower/optimizer function, instruction
and slot counts. Profile-level traces aggregate actual native analysis/pass
invocations and changes; phase-only observation must not enable pass profiling.

This is producer/representation acceptance, not execution or performance
acceptance. The CLI/VM journeys must independently establish actual execution
and compatibility before the v0.9 migration is complete.
