# HPIR physical runtime control

Run `dotnet run --project tests/HpirRuntime -c Release`. This is an internal
physical ABI control, not a substitute for compiling, binding, and running a
language program through the released CLI. It supplies hand-built HPIR to
isolate the executor from compiler decisions.

The main invocation goes through a fixed non-leaf call and 10,000 nested
guest calls, reading a parent field before its live mutation at the deepest
callee. It verifies value snapshots, native segment growth and reentry, exact
argument transfers, and normal returns with recording off and on. A
cancellation delivered on a nested segment transition checks failure
attribution and disposal of live stack segments. Further controls cover
the address snapshot of an indirect return destination and unaligned narrow
pointer arithmetic/access. The profile and timeline assertions check actual
growth and call-site source context. The heap controls include independently
measured allocation, reuse and tracing, and collection at a physical call
with a suspended caller's exact root, a transitive edge, a heap interior
borrow, and a non-heap stack borrow. The integrated control checks that only
the live heap objects survive and that the source-attributed collection
record reports the actual work.

The typed text control holds a live reader behind a gate while the program
constructs a nullable-test future. A real stdout write must complete before
the reader is released. It then reads an exact Number from a hexadecimal
line or returns the EOF alternative, without rereading the same completion
or retaining its host job.
