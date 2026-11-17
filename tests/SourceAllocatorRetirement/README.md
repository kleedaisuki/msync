# Source allocator retirement

`dotnet run --project tests/SourceAllocatorRetirement -c Release` checks that
storage/arena/slots are absent from embedded standard modules and public native
signatures, and that Storage is not a builtin/native-declaration type. Ordinary
user Type witness names remain legal. Real List construction/mutation journeys
run through source compilation and detached native artifacts at O0/O2, with no
historical Storage native type.

Format-6 bytecode and historical HIR require recompilation from source; there
is no private importer that can restore the retired allocator bindings.
