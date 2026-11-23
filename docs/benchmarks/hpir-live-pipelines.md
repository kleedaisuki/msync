# HPIR product CLI: live sensor and policy pipelines

2026-11-22 · Linux x64 Native AOT · official published 0.9.3 versus the
integrated development CLI. These are unmodified source programs from
the [application corpus](../../tests/Performance/Corpus/README.md);
independent oracle output is checked for every process.

## What was compared

- The [published baseline](hpir-0.9.3-baseline.md) is the promoted Linux
  x64 executable, SHA-256 `9edc10c0…`. It compiles and binds its own O2
  artifacts. The candidate is product Native AOT `msync`, SHA-256
  `911c7177…`, built with SDK 10.0.403.
- The HPIR source compiler is not yet the public CLI's default source
  route. A minimal internal producer executes the normal O2 HPIR
  lowering against byte-identical unlinked Compiler/Core/Hir/Optimization
  inputs of that exact CLI AOT publish. It writes fresh format-5 HPIR
  objects, which public `msync bind-ir`, `disassemble --json` and `run`
  consume. No earlier development image is cross-decoded. This is a
  public artifact execution measurement, not a source-mode speedup claim.
- Both implementations bind with the compile-time source path absent.
  All timed runs are in a separate directory, redirected through a real
  stdin pipe. Preparation is outside the timer. The `.mso` route includes
  the product's load/bind/run work; the `.mexe` is already bound. These
  are separate lanes, not averaged together.
- Seed 17, seven AB/BA pairs after one warmup, one logical CPU (6),
  telemetry disabled. Child CPU includes user and system time; wall
  includes process launch and runtime admission. Separate profile,
  diagnostics, and syscall runs never enter a timing pair.

| Application and distribution | Input demanded | Published → HPIR bound CPU p50 | Paired CPU change | Published → HPIR bound wall p50 |
| --- | ---: | ---: | ---: | ---: |
| Sensor, reference | 15,034 of 24,000 rows | 164.71 → 40.64 ms | −75.3% | 167.23 → 44.13 ms |
| Sensor, high rejection | 39,514 of 48,000 rows | 285.02 → 68.27 ms | −76.7% | 290.24 → 71.26 ms |
| Policy, reference | 28,000 rows | 314.08 → 66.41 ms | −77.7% | 319.21 → 69.83 ms |
| Policy, dominant target | 28,000 rows | 301.01 → 62.61 ms | −79.1% | 305.50 → 65.27 ms |

Sensor takes 12,000 accepted values in both distributions. Policy
executes 27,752 calls and 248 mutations in each distribution; the target
census changes from (15,611, 9,044, 3,097) to (26,550, 895, 307).
Published-to-HPIR paired CPU in the independently timed `.mso` lanes
was −71.5% / −74.9% for sensor and −80.6% / −79.5% for policy. The
dominant-policy object wall timing showed unrelated host load near the
end of the contrast window; bound/object CPU conclusions agree.

## How much depends on native selection?

Using the same HPIR bound bytes and product executable, the reference
sensor/policy paired CPU changes were −5.4% / −17.7% for
physical interpreter → adaptive. Eager native then saved another
10.2% / 13.1% relative to the adaptive runs. This establishes that the
large release-to-release-candidate gain cannot be attributed primarily
to native promotion. These are long-running input cases; the
[short/long gate](hpir-platform-gate.md) and
[fixed-input study](hpir-unmodified-corpus.md) separately account for
the publication cost on short inputs. Do not choose eager globally
based on these pipelines.

## Where the gain comes from

| Reference observation | Sensor | Policy |
| --- | ---: | ---: |
| Kernel reads / bytes, same on both executables | 50 / 51,200 | 135 / 136,853 (one EOF read) |
| Logical direct line requests, same on both | 15,035, including header | 28,001, including EOF |
| Incomplete reader tasks / reader-turn waits, same on both | 50 / 0 | 135 / 0 |
| Guest callback/target entries, same on both | 30,068 | 27,752 |
| Native direct guest calls, published → adaptive | 0 → 20,986 | 0 → 23,435 |
| Guest stack segment allocations, published → adaptive | 117 → 1 | 1 → 1 |
| Process managed allocation in profile, published → adaptive | 29.40 → 2.54 MB | 42.51 → 4.62 MB |

- **I/O is real and unchanged.** The same kernel-pipe identity was
  traced across duplicated stdin descriptors. Sensor's high-rejection
  variant also matched at 122 reads / 124,928 bytes. The sensor's accepted
  limit stops both implementations before EOF, with conventional
  buffered read-ahead. Diagnostics record direct reader dispatch at
  every line, not a thread-pool dispatch per line. Only the buffered
  reader's actual read boundaries suspend. This rules out reduced I/O
  work, skipped rows and
  eager whole-stream reads as explanations for the measured gain.
- **Callbacks are real; the dispatch scaffolding is not.** Sensor still
  invokes parse and predicate callbacks once per demanded line in both
  distributions. HPIR emits direct guest calls, and needs one physical
  guest stack segment even at 39,514 pulls (published allocation count:
  681, with the same 79,028 callback entries). Policy still dynamically
  selects a closure with live captures and preserves all three target
  counts and the exact output. The 27,752 published `ranges.at`
  import calls disappear: HPIR uses an ordinal and native pointer
  addressing over the known vector layout, followed by finite target
  dispatch. Both retain the work required by the source.
- **Promotion uses the live continuation.** In separate diagnostics,
  sensor promoted once at the main backedge, HPIR PC 1382 → 1345, after
  178,956 guest instructions against a 178,944 budget. Policy promoted
  once at the selected guest call, PC 1662 → target function 1, PC 0,
  after 220,165 instructions against a 220,160 budget. Observed
  compile-plus-activation spans were 0.94 ms / 1.24 ms. Uninstrumented
  latency is the timing table, not the diagnostic duration or a
  subtraction of overlapping spans.

The HPIR census counts *more* VM instructions than the published
runtime for both applications. Raw opcode counts across IRs do not
explain the timing. Keeping physical layout, call targets and stack
ownership available does: fewer managed activations, host boundaries
and stack reservations, not less consumption or a proof-enabled
special case. The remaining repeated typed service boundaries are the
real Text pull and ready Number parse, once each per data row. If they
become the next bottleneck, measure their cost directly before adding
any producer/consumer fusion; preserve per-line demand, EOF and parse
error source attribution.

The exact product binary, producer inputs, independently generated
fixtures, raw timing pairs, separate SQLite census/timeline receipts,
and filtered kernel-pipe traces are under
`artifacts/benchmarks/hpir-cli-live-pipelines/`. The remaining release
gate is to repeat representative comparisons after public source
production and packaging switch to HPIR; these timings are evidence
for current artifact execution only.
