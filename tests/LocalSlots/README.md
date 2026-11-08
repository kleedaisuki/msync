# Optimized local storage

Run `dotnet run --project tests/LocalSlots` from the repository root. The probe
executes bytecode before and after slot compaction and binary serialization,
including shared closure locations and iterator state. It also checks the actual
O1/O2/O3 pipeline rather than only invoking the isolated transformation.

Capture and parameter slots retain their ABI prefix. Persistent REPL entry slots
retain externally owned identities; private function frames can still shrink.
The transformation scans instructions and the slot table once, without changing
branch positions or copying captured payloads.
