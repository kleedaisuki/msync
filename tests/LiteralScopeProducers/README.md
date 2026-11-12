# Literal Scope producer acceptance

Run the focused semantic and host-storage checks with:

    dotnet run --project tests/LiteralScopeProducers -c Release

These exercise the same demand program through compiled source, a read-back
format-6 portable artifact, and a separately bound/read-back artifact. They
check fresh identity on every pull, existing aliases after first mutation,
a deferred side-effecting suffix, physical frame bypass and unchanged logical
instruction counters, and detailed timing spans. A duplicate wire layout is
rejected even when its factory would never be demanded. A legal opaque field
projection retains its original diagnostic and span if selected, and zero
demand does not execute it. Mutable REPL generations retain the canonical path and
continue to observe live captures.

The first-write test consumes 2,051 actual VM-produced rows from one recipe.
Two host threads race distinct first writes to 2,048 rows; a separate untouched
row receives a Cell as its first mutation and shares subsequent writes with
that Cell; a host record copy retains the original's storage alias without
sharing mutations with other template instances; one sentinel remains unchanged.

The readonly producer checks project literal Numbers by exact field name
and offset without constructing a row, preserve signed-zero and NaN bits,
and verify that replacing a Range's `Items` changes what is enumerated.
An ordinary pull still creates a fresh Scope. The actual enumerable
honors both its original producer token and the selected consumer token;
an empty producer has no item token to poll. A partial recipe does not
prefetch its effectful suffix, which executes once per actual demand.

To verify the same source, stdout and serialized SQLite diagnostics with a
published CLI, including deleting the source before portable execution and
binding and deleting the portable input before bound execution:

    python3 tests/LiteralScopeProducers/journey.py /absolute/path/to/msync

The migrated Release suite passed 50 producer contracts plus the readonly
witness checks. The format-6 Linux x64 Native AOT CLI also passed the source,
detached portable, and detached executable journeys; each recorded four recipe
spans and no physical factory calls.
