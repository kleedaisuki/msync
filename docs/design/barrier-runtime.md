# Barrier runtime and physical ownership

## Contract

The new execution model deliberately removes transaction scopes, retained undo
history, MVCC enrollment, and native filesystem-view providers. External programs
run against the actual filesystem; their effects are never inferred to be pure,
snapshotted for rollback, or replayed after failed speculation.

Compilation expands hygienic macros and emits an ordered sequence of basic blocks.
The interpreter admits only the current block. A launch block completes when its
child is owned and its output drainers are running, not when that child exits.
An await block establishes natural process completion and completion of its
connected port flows before the next block is interpreted. Macro-local async
children have targeted implicit awaits at macro exit. Untargeted await joins all.

Within a block, dependency edges distinguish admission from completion. A stable
source-ordered ready frontier dispatches independent finite assignments up to the
configured copy capacity. Runtime observations specialize only this block;
failed speculative guards retain the original instruction sequence. No future
block filesystem state is bound ahead of an external program that could create
or remove those files.

## Physical owners

| Owner | Lifetime and guarantee |
| --- | --- |
| `RuntimeSession` | Retains finite workers, children and flows, even when an individual await future is dropped. |
| `ordinary::Ordinary` | Captures finite input and owns statement publication/compensation, then retires payload. The filesystem durable batch/registry supplies crash recovery, not process rollback. |
| `process::Process` | Owns native child exit notification plus immediate stdout/stderr spool drainers. Memory is bounded by buffers; output disk quota is explicit, not fictitious RSS accounting. |
| `program::Program` | Owns single-consumer stdout/stderr and single-producer stdin. Scalar readers observe retained completion without blocking the dispatcher. |
| `flow::Flow` | Owns one transfer. EOF settles only that flow. Native regular input is captured before background admission so later overwrite/deletion cannot change its bytes. |
| `Acquisition` | Retains a started blocking flow-source capture before the destination is admitted. The worker owns its stream/buffer lease and a backing identity clone; abandoning an await cannot release either early. |
| `scheduler::Admission` | Separate copy/program/stream slots with one aggregate payload-buffer pool. Copy slots are independent of child slots so a child can wait for a later finite file producer without occupying its worker. |

Bare native calls launch and await. Explicit async calls retain open stdin until a
producer attaches or await/shutdown closes unused stdin. Both outputs drain from
launch, including calls whose only later observation is an exit code. Completed
programs remain known to the lexical runtime for later scalar/spool access; they
no longer consume active-native capacity.

Normal cancellation stops new admission, cancels finite/physical stream work,
and then drains already-started external children to natural exit. A program
waiting forever can therefore keep graceful shutdown waiting forever. Explicit
force-stop, OS process killing and power loss are outside this program-lifetime
guarantee. Atomic external effects are not promised under these events.

## Design references

- [Tokio Child](https://docs.rs/tokio/latest/tokio/process/struct.Child.html): dropping a child is not joining it; retaining its drainers also prevents accidental pipe closure/SIGPIPE.
- [Rust Command](https://doc.rust-lang.org/std/process/struct.Command.html#method.new): preserve native executable search and exact argv instead of reimplementing PATH or silently inserting a shell.
- [Tokio select cancellation safety](https://docs.rs/tokio/latest/tokio/macro.select.html): the flow pump accounts individually accepted writes rather than cancelling `write_all` and losing its accepted prefix.
- [Tokio AsyncWriteExt](https://docs.rs/tokio/latest/tokio/io/trait.AsyncWriteExt.html): explicit shutdown delivers stdin EOF; unrelated endpoints are not globally cancelled.

This note describes implementation ownership, not acceptance evidence. Actual test
and release results belong in the validation record.

## Transport identity

A WSL/SSH launch is currently owned through its local transport process. Its PID
port identifies that host-side launcher, not an invented guest PID. Exact guest
process-tree supervision would require a separate remote handshake/protocol;
this implementation does not infer it from the executable's pathname. Normal
cancellation still leaves the transport alive and drains it rather than sending
an implicit termination request.

## Runtime proof refinement

Background stream nodes retain a running state until their actual EOF. Block
admission can finish with such flows retained, but a completion edge within that
block is not silently relabelled as completion. A captured native file input
proves its read side has already completed; `BlockSchedule::captured_input`
releases only outgoing pure file-read hazards. It never waives a destination
write, stream consumption, directory namespace, or mixed dependency.

FIFO rendezvous has stronger, endpoint-specific evidence. The current block
retains metadata-only native generation handles, pairs source-ordered writer and
reader waves, reserves their combined stream buffers before starting a member,
and carries matching identities through dispatch and actual open. A pathname
alone is not such a proof. Named pipes and ordinary Windows filesystem paths
also belong to different OS namespaces; their syntactic distinction does not
require opening a pipe as an allegedly harmless metadata probe.

A FIFO cohort is reserved only after **all** its incoming dependencies from
outside the cohort are satisfied. One ready member is not sufficient: reserving
its peers' slots while an earlier TCP producer still needs one can exhaust the
very budget needed to release the cohort. `BlockSchedule::coadmission_ready`
checks those external gates without contracting nodes or waiving outgoing
dependencies. Each member still follows the ordinary admission frontier after
the combined reservation; this is distinct from speculative graph contraction.

Speculative overwrite groups are admitted only after every external incoming
edge is satisfied. Outgoing admission edges disallow contraction. A failed guard
restores the complete original group to the pending frontier and disables all
members for further folding in that block; no completed prefix is replayed and
no quadratic series of progressively shorter retries is attempted.

A session allocates one invocation backing lazily. Its empty root is durably
registered before child assets, process spools or imported bytes are placed in
it. Finite owners allocate beneath that root and exclude its verified identity
from user-tree captures. They do not hide every `.temp` directory or guess an
`execute-*` prefix. The startup registry remains beneath the configured stable
scratch root, so a later process can discover a crashed invocation. Shutdown
retires the registration only after all physical owners have joined, program
spools are released, and the backing is actually removed.

Graceful shutdown is distinct from explicit stop: it closes physical/input flows
but drains program-output/scalar captures through their spool EOF. This preserves
`P::stdout -> file` followed by REPL quit. Explicit `/stop` or Ctrl-C may cancel
that capture, while still not terminating the child. `/kill` is separately named
and explicitly destructive. A completed native owner caches both success and
failure, so a spool error after natural exit is terminal work rather than a
permanently active phantom job.

A launch fence is not a global drain of previously admitted background flows.
For example, an async server's stdout capture may remain live while a later
async client is launched: waiting for that capture's EOF before admitting the
client would create an artificial deadlock. Finite work from the preceding
block is complete, background owners remain retained, and an explicit await
establishes the requested join. A directional after-fence uses its current-block
dependencies rather than joining every unrelated flow in the session.

Dropping a live session requests stream/finite cancellation but never native
force-stop. One asynchronous retirement owner retains all worker handles,
programs, resource leases, the private invocation and its stage registration
until natural child/drain completion and cleanup. The Tokio runtime itself must
remain alive for this best-effort Drop path. Destroying that runtime is not a
graceful join; the CLI therefore explicitly awaits shutdown on normal paths.

Flow acquisition has its own retained owner because
[started `spawn_blocking` work cannot be aborted](https://docs.rs/tokio/latest/tokio/task/fn.spawn_blocking.html).
A local join handle inside a cancellable submit future is insufficient: dropping
that future would detach the actual copy while releasing its admission budget
and allowing backing cleanup. The session installs `Acquisition` before awaiting
its worker. Success transfers the captured source and the same lease directly
into the admitted flow. A later submission, explicit shutdown or asynchronous
Drop retirement instead joins and discards an abandoned capture; it never
replays the unadmitted destination. The captured source is dropped and its
private child storage cleaned before the outer stage registration is retired.

Generation-6 Windows direct tests exercised this protocol with a gate inside the
actual blocking capture worker, after it had acquired real file bytes. All five
`execution::live::cleanup_tests` passed, including abandoned capture followed by
shutdown, session Drop retirement, and a new submission with no replay of the
unadmitted TCP destination. The tests verify the occupied admission lane, live
backing/record until worker release, unchanged source and eventual cleanup. The
same frozen image also passed seven flow unit tests and all thirteen outer
`program_execution_v4` journeys (one explicitly invoked child fixture is ignored
by the outer harness). Evidence: `.temp/barrier-runtime-gen6-{cleanup,flow,program}.log`
and the matching image/result JSON files. CLI SHA-256:
`914A0B641E8A7B85844B0E6277E0BFE016082323F0058F796A0ABE39F2D19186`.
These are selected Windows gates, not a substitute for the complete cross-platform
release and performance gates.

Native stream classification covers TCP, syntactic Windows named pipes and
current Unix FIFO/socket metadata without opening a peer. A ready directory
source cannot enter a physical destination: admission rejects it before creating
private workspace, with guidance to explicitly serialize it through a program's
stdout. This validation runs at the ready instruction, rather than trusting a
block's earlier observation or waiting for a detached flow failure. Flow failures
with retained FIFO generation proofs carry generation/recovery guidance based on
those proofs, not diagnostic string matching; the native cause remains intact.
