# Canonical file and command journeys

This executable exercises the source-backed `files::{File,Directory}` and
`commands::{Command,Completed}` families through their explicit-self operations.
Instances are data records; none of the fixtures depends on removed factory
functions or per-instance method closures.

The file journey checks Unicode writes and appends, independently constructed
records, shared record identity after a move, a real copy, and directory
snapshot/filter traversal. The command journey starts this executable as a real
child process, sends Unicode input, selects the local endpoint, preserves the
original argument snapshot, and publishes captured output.

Both programs run at O0/O2/O3 as source, serialized `.mso`, and context-bound
`.mexe`. The 72 observations compare actual values and filesystem bytes. Build
the test executable against one coherent Compiler/Core/Runtime snapshot; do not
rebuild shared product outputs concurrently with other owners.

The canonical migration passed all 72 observations against the immutable managed
Release snapshot recorded in
`.temp/canonical-source-migration/snapshot-1/snapshot.json` (Compiler SHA-256
`ace1a853119d3f41ddbca0f505c5b7da78ab34707fcec7379cb3218a9ac690a7`). Only
the test executable was rebuilt; the compiler and runtime were reused unchanged.
This is managed integration evidence, not a Native AOT timing or platform claim.
