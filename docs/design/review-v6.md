# Independent 0.6 integration review

The implementation is under active integration. This note tracks concrete
cross-component lifetime issues and their disposition, not a release verdict.

## Slot allocation across lexical function scopes

`symbols::instantiate` originally constructed a fresh default `Scope` without
sharing the caller's runtime slot allocator, while macro instantiation correctly
shared it. A symbol/boolean containing a nested range callback could consequently
allocate slots already used by its caller. The compiler owner has been notified
to share the allocator for all lexical/script subscopes. Verification should
exercise a range predicate that itself evaluates another range predicate and
reads its outer parameter after the inner query.

## Process identities in repeated regions

The existing `live::spawn` rejects an already instantiated process identity.
Reusing a compiled loop body's static `Spawn` identity on iteration two therefore
cannot implement repeated launches without a runtime instantiation step. The
runtime owner has been notified. The desired model is fresh per-iteration process
identities with consistent references in ports/awaits, while preserving handles
for genuinely retained asynchronous work. A two-iteration `for` containing a
program launch is the discriminating public-CLI journey.

## Reducer precedence

The initial parser gave range `|` lower precedence than logical `and`/`or`, so a
boolean-producing reducer could not naturally combine with another predicate:
`paths/<.+> | ranges::empty or true` parsed the logical expression as the adaptor.
The frontend owner has been asked to align pipe precedence above logical
operators, consistent with the familiar C++ operator ordering, or make the
parenthesized-only requirement an explicit contract.

## Branch lowering side tables

Initial constant-branch folding discarded instructions without discarding their
relocations, source origins or pending processes. A dead asynchronous launch
inside a macro could escape as a join of an undefined process. Both arms also
shared the linear-port consumer map, incorrectly rejecting exclusive consumers.
The compiler owner changed lowering to snapshot each arm's ownership metadata,
commit only the selected static arm and merge conservative dynamic-arm state.
Region-local pending launches now have region-local joins. The object validator
similarly needs path-aware consumer/availability handling.

## Asynchronous flow admission

Initial singleton control dispatch called `join_all_flows` before every control
operation. This implicitly acquired a previously released network stream even
when checking an unrelated ordinary path. An open peer can therefore prevent
later producer/marker code from executing. The runtime owner has been notified.
The regression holds a real TCP peer open, requires an unrelated `exists` branch
to publish its marker, then closes the peer and drains the CLI with bounded waits.

## Regression artifact

`tests/review_v060.rs` contains six independent public-interface probes covering
all five issue groups above. The code has been formatted; execution results are
recorded after the root's incremental integration window. Frontend precedence
was corrected and tested by its owner in the isolated actual-source harness.

## Integration disposition

The root's focused `cargo test --locked --test review_v060` gate passed all eight
review journeys; output is retained in `.temp/v060/review.log`. In addition to the
six original public-CLI probes, the target rejects a flat 1024-stage forged
pipeline without executing it, and runs a filename-string existence/deletion
journey with a bound logical cwd different from the embedding process cwd.

Source rechecks confirm shared callback slot allocation, per-arm compiler side
metadata, branch-local object process availability, bounded control nesting,
128-stage object pipeline limits and removal of the implicit global flow join.
The runtime also retires completed fresh lexical-region process records rather
than retaining unused output handles across every loop iteration. Dynamic program
arguments are a subsequent integration change and are not covered by this gate.
