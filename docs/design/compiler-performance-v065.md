# v0.6.5 compiler performance acceptance

The current delivery image is `f9162114...`, not the initially accepted
`e18d9f36...` image. The original measurements and bounded decision below remain
attached to e18; the later Windows-root correctness repair and its narrow
[delivery refresh](#delivery-refresh-after-windows-root-canonicalization) have
separate identities and rows. In particular, an e18 timing improvement is not
automatically a claim about the delivery image.

## Workload and decision boundary

The type-system work must preserve practical compilation costs while making
operator domains and scope/function composition more general. This note owns
pure frontend lowering, not SSH transport, runtime publication or preparation's
filesystem observations. The maintained `benches/compiler_v6.rs` harness parses
and constructs source outside timing, then measures fresh `Session::compile_named`
work and destruction. Allocation calls/requested bytes are collected in a separate
untimed pass; they are cumulative traffic, not RSS or peak memory.
The probe also reports actual public control `Type`, `Expr`, control-instruction
and HIR-instruction layouts: a
union representation can increase every expression's footprint even when an old
workload never constructs a union. Layout is causal context for allocation
traffic, not an independent reason to replace the type model without measurement.

Three unchanged, representative workloads discriminate the relevant mechanisms:

| Case | Input construction | Reason for inclusion |
| --- | --- | --- |
| `symbols` | One lexical path function called N times in transfers | General operator capability and specialization overhead |
| `conditions` | N runtime `exists` conditions with both arms | Bool checking, control lowering and origin bookkeeping |
| `captures` | N path bindings and lexical symbols capturing the preceding value | Persistent environment growth and snapshot/capture costs |

Use N=100 and 1,000, one warmup, then seven timed samples. The final comparison
must use the same maintained probe source linked separately against the frozen
baseline and integrated candidate libraries. Run both arms in alternating order
without builds or competing performance probes. Preserve individual samples when
making paired claims; summary minima/maxima alone cannot establish paired wins.
The maintained `scripts/bench-compiler-v065.sh` runner uses seven alternating
AB/BA process pairs with one timed sample per arm/case. Each case still performs
its own warmup and separate allocation pass. It retains every row and verifies
unchanged executable hashes at both ends, rather than treating two medians as
seven paired observations.

The explicit `typed` case is a **candidate-only absolute-cost** workload: repeated
scope qualification of a renamed lexical function, Bool function expansion, a
mixed Path/String/Bool literal range, and `$view::transform` with an argument-
independent projection. The old parser does not support this capability; its
rejection is not a useful baseline timing or a speedup denominator. Existing
default benchmark cases are unchanged; select `typed` explicitly when supported.

`benches/compiler_cli_v065.py` supplies one real-process control: `check` of 100
unchanged symbol/path calls, paired between the two actual binaries. It separately
measures `check` of the candidate-only typed fixture. It reuses the existing
bounded, effect-checking process helper from `config_startup.py`, isolates
HOME/config roots, disables configuration explicitly, verifies no unexpected
files, and checks image hashes. These process intervals include startup and
parsing; they are not the pure-lowering intervals above.

## Frozen baseline and reproduction

The SSH owner built and preserved exact source commit
`d501fd26614055af7a0f77b58a7b20bbb6cadd99` once. Compiler measurement reuses that
library rather than starting a duplicate Cargo build:

- `target/performance-v065/baselines/d501fd2-source.tar.gz`, SHA-256
  `7ffc47c87b0e9cd806412a3898758c64b927486205ea83d144e763c9bc07e695`;
- `target/performance-v065/baselines/libmsync-d501fd2-before.rlib`, SHA-256
  `eba0934ef251a5caaf2b58f4d82ba909aebb8b01fdc2a0e34d9e29a383f87308`;
- `target/performance-v065/baselines/msync-d501fd2-before`, SHA-256
  `dca16a83beb10131ecde492ba6ff587dd90478bfe0dc4450d41088b779fff8e8`.

The original dependency artifacts are in `target/release/deps`. The host is Linux
x86-64, Rust/Cargo 1.99.1, LLVM 23.1.1. The matching toolchain/kernel/environment
record lives beside the frozen artifacts. `statfs` uses a generic ext2/ext3 label;
it does not establish filesystem cache or physical storage behavior for this
pure compiler measurement.

Build a probe without rebuilding the library:

```sh
CARGO_MANIFEST_DIR="$PWD" rustc --edition=2024 -C opt-level=3 -C debuginfo=0 \
  -L dependency=target/release/deps \
  --extern msync=target/performance-v065/baselines/libmsync-d501fd2-before.rlib \
  benches/compiler_v6.rs -o target/performance-v065/probes/compiler-v6-before
MSYNC_COMPILER_SIZES=100,1000 MSYNC_COMPILER_CASES=symbols,conditions,captures \
  MSYNC_COMPILER_SAMPLES=7 target/performance-v065/probes/compiler-v6-before
scripts/bench-compiler-v065.sh /absolute/path/to/before /absolute/path/to/after \
  .temp/v065/compiler/paired.log
python3 benches/compiler_cli_v065.py /absolute/path/to/before-msync \
  /absolute/path/to/after-msync .temp/v065/compiler/cli.csv 7
```

These commands require the matching retained dependencies and are not a claim
that an arbitrary release rlib is interchangeable with this exact baseline.

## Cost preflight, not final before/after evidence

A short baseline-only discovery window checked that the selected workload sizes
are inexpensive enough for repeated matched measurement. No Cargo/rustc process
was active at admission; the SSH owner had no timed cohort. Three samples after
warmup produced:

| Case | N | Median ms | Allocation calls | Requested bytes |
| --- | ---: | ---: | ---: | ---: |
| symbols | 100 | 0.543012 | 4,180 | 1,091,224 |
| conditions | 100 | 0.627377 | 3,057 | 1,104,810 |
| captures | 100 | 2.031548 | 12,905 | 1,139,471 |
| symbols | 1,000 | 9.273723 | 41,242 | 10,250,316 |
| conditions | 1,000 | 8.650845 | 31,495 | 12,567,798 |
| captures | 1,000 | 73.044599 | 180,701 | 21,309,530 |

The preserved preflight probe SHA-256 is
`004aeab7f538f37a5baafb609a2a537650930868871703ee6ac1677258c63da3`,
with exact source SHA-256
`dcb6664f235094e3bde4d2d268be7643fe60fb8ca88152e4d0b66c51984dcd9f`.
Both are retained under `target/performance-v065/probes/` with `preflight` names.
Raw discovery rows are disposable under `.temp/v065/compiler/`; the table above
preserves their useful sizing conclusion. The later `typed` case addition changes
the common probe source, so both final arms must be rebuilt from that same source.
Do not compare a new candidate against these preflight medians or relabel them
as paired final results.

## Layout correction and exact final candidate

An early source-shape probe extracted the exact baseline and first candidate
`Type` enum definitions and compiled them with the matching Rust toolchain.
The original four-unit-variant type occupied 1 byte; adding `Union(Vec<Type>)`
made it 24 bytes. This is useful layout evidence for compact finite scalar-union
storage before candidate freeze, not a built-candidate `Expr` size or a latency
regression claim. The permitted range-item universe has only Bool/String/Path;
the language owner can retain readable union serialization without making every
scalar type carry a heap-vector-sized payload. The finite `ScalarUnion` payload
has now replaced the initial vector in production source; final built layout and
behavior are still checked on the integrated artifact below.

The final built candidate is version 0.6.5, from the root's frozen integrated
source archive, SHA-256
`7f08a7c0b9b34f055c026df2052be96b94cbbd7f7f54a63feb0c17003ffe163b`.
Its library SHA-256 is
`c85145101acff3c1ff1f7534fe1f1b775ca6658bed701c9ed5604f8bcb5b5c05`,
and executable SHA-256 is
`e18d9f36b905630b9915cc5634e4ff4833a66ee34924650fc555913482433f7f`.
The common probe source SHA-256 is
`4a3f56219cea233075d5b6fa0070b4ab3dae6929cfe015a6bda82d8276d76ca3`;
baseline/candidate probes are respectively
`357a0c34e85978f169aa47e4b584c8ff112104d55468425aa8ff4e0074bbad53`
and `702e1ef14b27165ebbc30fb05951b7b4982628dd148f5f3ed52e847a0aa13226`.
Frozen source, library, binary and probe artifacts live under
`target/performance-v065/{baselines,probes}`. Compiler data below is not relabeled
from the earlier discovery or an SSH-only candidate.

Actual public layouts are identical in both linked artifacts:

| Type | Baseline bytes | Candidate bytes |
| --- | ---: | ---: |
| Control scalar `Type` | 1 | 1 |
| Control `Expr` | 152 | 152 |
| `ControlInstruction` | 336 | 336 |
| HIR `Instruction` | 384 | 384 |

Thus the finite union adds no all-expression footprint tax on this target. This
is a stronger concrete result than the initial vector shape; it does not by
itself prove every compiler workload is faster.

## Final matched lowering observations

Root granted an exclusive window after the final-image functional and actual
editor-host checks ended. Seven alternating process pairs ran unchanged cases
from the same probe source; no Cargo/rustc was active at admission. All probe,
library and executable hashes matched their frozen identities afterward. Every
row is retained in [compiler-v065-lowering.csv](compiler-v065-lowering.csv).

| Case | N | Before median ms | After median ms | Paired mean delta ms | After faster / 7 |
| --- | ---: | ---: | ---: | ---: | ---: |
| symbols | 100 | 0.794014 | 0.887128 | +0.073723 | 2 |
| conditions | 100 | 0.903820 | 0.895245 | -0.051139 | 5 |
| captures | 100 | 2.106090 | 1.669094 | -0.319064 | 6 |
| symbols | 1,000 | 9.512848 | 8.747402 | +0.994468 | 4 |
| conditions | 1,000 | 9.729904 | 8.082937 | -1.863981 | 7 |
| captures | 1,000 | 67.290268 | 67.446038 | -4.745387 | 4 |

Allocation calls and requested bytes are **exactly unchanged** for all six
case/size combinations, including 41,242 / 10,250,316 bytes for symbols at 1,000,
31,495 / 12,567,798 for conditions, and 180,701 / 21,309,530 for captures. Root's
separate semantic acceptance validates the new capabilities; this comparison
isolates the cost on older working source, not a parser rejecting new syntax.

Results are not uniformly positive. Symbols at 100 are slower in five pairs,
with a +0.124318ms paired median delta. Symbols at 1,000 have an improved median
but a +0.994468ms paired mean because candidate tails reached 15.367315ms versus
baseline 10.423340ms. Captures at 1,000 are mixed despite identical allocation
traffic. Conditions at 1,000 improved in all seven observed pairs, but this is
not a universal compiler speedup or a strict no-regression bound.

The new typed workload's candidate-only lowering median is 4.387292ms at N=100
and 40.377859ms at N=1,000 (seven samples each). Requested bytes are respectively
2,621,107 and 26,449,939; allocation calls are 17,044 and 176,979. Each entry
introduces three bindings and combines scoped function results, Bool expansion,
mixed literal lists and `$view` projection. An old-parser failure is deliberately
not used as a denominator.

## Real CLI control and rejected size

The first proposed 1,000-transfer CLI control hit the old baseline's existing
15-second process deadline during its first warmup, before any candidate CLI
invocation or accepted CLI row. That size magnifies static graph/proof materialization beyond the
pure lowering question; it is not evidence that new type inference regressed.
The bounded helper killed/joined that child. Its failed header-only output remains
explicitly named `cli-rejected-1000-final-e18d9f36.csv` under `.temp/v065/compiler`.
The timeout was not enlarged and failure was not silently converted to a sample.

Root approved a separate follow-up window after all 60 SSH measurements and
fixture cleanup. Only the extra real CLI control was reduced to 100 statements;
the 1,000-entry lowering comparisons above were retained. A small SSH summary
write was reported around the start of the CLI warmups; the timed rows followed
those warmups, with no builds or other workload processes admitted.

The final helper records explicit entry counts and image identities in
[compiler-v065-cli.csv](compiler-v065-cli.csv). Its size correction is measurement
orchestration only, not a change to the frozen binary/library or common lowering
probe. Its final source is copied beside the probes with a separate digest.

| Real command | Before median ms | After median ms | Paired mean delta ms | After faster / 7 |
| --- | ---: | ---: | ---: | ---: |
| `check`, 100 symbol/path transfers | 574.871219 | 546.640591 | +21.379670 | 3 |
| `check`, 1,000 typed entries (candidate only) | — | 89.916579 | — | — |

The apparent old-source CLI median improvement does **not** establish a reliable
speedup: only three paired checks are faster, paired median delta is +11.214367ms,
and candidate range is 523.293254–746.139470ms versus baseline
512.468872–630.277045ms. This mixed end-to-end result remains visible alongside
the pure compiler mechanisms; there is no claim that more expressive types made
every check faster. The candidate-only typed check range is 71.673536–102.394864ms.

The small-symbol and CLI-tail observations were raised to the root before
performance acceptance. The root authorized one small-symbol discriminator and
retained the mixed CLI result without another broad cohort.

## Small-symbol discriminator and bounded decision

The follow-up changes only the probe's timed loop to 50 fresh compilations,
dividing the duration by 50. It retains the same frozen libraries, source
fixture, allocation pass and seven AB/BA pairs. These are seven paired averages,
not 350 independent samples per arm. The source and resulting probes are kept
under `target/performance-v065/probes` with `batched` names:

- source SHA-256 `e2f5f906b944d339001c0b37bc06a15822f7a91f01e8f6cd596b0a78ec2eeb06`;
- baseline probe `9e69f6596136ebf4148d22681ade0cdb3262967b7996a22d0cd07b792ee490d0`;
- candidate probe `ee9b4bdb79b35080427a67cf4360e95d0e3c9b4ef813e5f15a66541f04e73344`.

All [14 batched rows](compiler-v065-symbols100-batched.csv) remain separate from
the first cohort. Per-compilation medians are 0.802682→0.784103ms; paired mean
delta is +0.022880ms, paired median +0.024574ms, with the candidate faster in
three of seven pairs. Allocation count/requested bytes remain exactly
4,180/1,091,224 per compilation and all four public layouts remain unchanged.
Image/library hashes were verified again; no production code changed.

The initial approximately 0.1ms small-case penalty is not a stable observation
under this batching control. A small positive paired residual remains mixed;
neither dataset establishes a causal fixed type-checking cost or universal
zero-cost bound. The practical decision is to retain the more expressive model
and its compact finite representation, not add speculative caches or rewrite
production for this sub-millisecond variability. The larger condition workload
is consistently faster in the observed pairs, while symbol/capture/whole-CLI
tails are explicitly mixed. No blanket compiler speedup or strict absence of
all regressions is claimed.

All compiler performance work is now closed at this bounded scope. No more
timing, builds or production optimizations are pending from this investigation.

The maintained paired runner passed Bash syntax checking and mocked two-pair
AB/BA order, invalid-count, existing-output refusal and child-exit propagation
checks. Those orchestration checks do not rerun or validate the compiler itself.
The real-CLI helper's function annotations, syntax, explicit entry counts, arm
selection and isolated/no-config invocation behavior were checked; its actual
final-image invocations completed successfully. Rust probe formatting and all
measurement-document local links/diff checks pass. Semantic capability and
restart/publication acceptance remain the corresponding owners' actual tests,
not benchmark timing assertions.

## Delivery refresh after Windows root canonicalization

Native Windows tests subsequently exposed a shared `LogicalPath` defect: known
verbatim drive roots were treated as uncertain (causing false global joins), and
verbatim/ordinary UNC roots could fail to alias. The central repair in commit
`8818d117172d67ddb9b4145ea645ccc759b20c0f` canonicalizes the known roots with
borrowed handling for ordinary Linux paths. This changes a shared hot-path source
file, even though the inferred type/specialization implementation is unchanged.

The new delivery executable SHA-256 is
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`,
and its library SHA-256 is
`0ef4f615b02f4f8b611d4cd3d54d60d9d06cb161b9f10192aa39b543bc7c3aeb`.
The unchanged common source `4a3f5621...` was linked to that exact library,
producing delivery probe SHA-256
`84ade265c5c295d095a603e848dab7d6c6514a7dc9b13557410472229d850564`.
The original d501 baseline/probe was reused unchanged, not an e18-based control.
The artifacts and digest manifests remain under `target/performance-v065`.

Root granted one narrow refresh after the delivery image's 11 functional cases,
actual editor-host acceptance and four SSH metadata checks finished. No
Cargo/rustc was active at admission. Seven AB/BA pairs reran only the three
primary lowering cases; candidate-only typed lowering was also refreshed.
Image/library/probe hashes matched afterward. No real-CLI or small-batch catalog
was repeated, and no e18 row was overwritten. The complete
[84 delivery rows](compiler-v065-delivery-lowering.csv) are separately inspectable.

All four public layouts still match the old baseline exactly: Type 1 byte,
Expr 152, ControlInstruction 336, HIR Instruction 384. Every case/size also retains
exactly the same allocation count and requested bytes as the old baseline and
the e18 checkpoint.

| Case | N | Before median ms | Delivery median ms | Paired mean delta ms | Delivery faster / 7 |
| --- | ---: | ---: | ---: | ---: | ---: |
| symbols | 100 | 0.713073 | 0.839765 | +0.181948 | 1 |
| conditions | 100 | 0.724154 | 0.982094 | +0.198826 | 2 |
| captures | 100 | 1.827452 | 1.999640 | -0.044137 | 3 |
| symbols | 1,000 | 6.615947 | 9.769462 | +1.005131 | 3 |
| conditions | 1,000 | 7.580865 | 8.064902 | -0.984294 | 3 |
| captures | 1,000 | 64.817044 | 63.200809 | -7.954044 | 4 |

The refreshed timing is weaker than the earlier e18 cohort, and remains visible:
small symbols are slower in six pairs (paired median +0.137414ms), and small
conditions in five (+0.210375ms). Symbols at 1,000 have paired median +2.763090ms,
despite overlapping ranges of 6.178152–11.285696ms before and
6.607969–10.803209ms after. Conditions at 1,000 are now mixed rather than the
seven-of-seven improvement observed for e18; their paired median is +0.126643ms
while the paired mean is negative because of a baseline tail. Thus neither the
e18 condition speedup nor its small-symbol discriminator is relabeled as proof
of a delivery-image speedup or zero-cost guarantee. Unchanged layouts/allocations
rule out a footprint increase on these controls, but do not identify the cause
of timing variation or prove an absence of all CPU costs. These observations
were reported to the root before acceptance.

Candidate-only typed lowering on the actual delivery image has medians
4.244709ms at N=100 and 42.295995ms at N=1,000, with observed ranges
4.183185–5.069388ms and 34.580554–48.489313ms. Allocation calls/requested bytes
remain 17,044/2,621,107 and 176,979/26,449,939 respectively. These are new-capability
absolute costs, not an old-parser speed ratio. The negative single-compilation
observations prompted the bounded amortized control below.

### Delivery amortized control and accepted cost

Root authorized one follow-up covering only symbols and conditions at N=100 and
1,000. It reuses the existing 50-repeat source
`e2f5f906b944d339001c0b37bc06a15822f7a91f01e8f6cd596b0a78ec2eeb06`
without edits, the original d501 batched probe `9e69f659...`, and a separately
linked delivery probe SHA-256
`ee0ef7d228ce0d0f3aa744fb87092a91ad7953063546ba10e72b787956f63726`.
The binary/library identities stay f916/0ef4. Seven AB/BA pairs each measure
50 fresh compilations and normalize by 50; these are seven paired averages,
not 350 independent samples. All [56 amortized rows](compiler-v065-delivery-amortized.csv)
are preserved independently of the first delivery and e18 cohorts. End hashes,
allocation traffic and all four public layouts remained unchanged.

| Case | N | Before median ms | Delivery median ms | Paired mean delta ms | Paired median delta ms | Delivery faster / 7 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| symbols | 100 | 0.765136 | 0.740543 | -0.014392 | -0.008535 | 4 |
| conditions | 100 | 0.647579 | 0.707268 | +0.050031 | +0.014621 | 3 |
| symbols | 1,000 | 7.911252 | 8.205301 | +0.281366 | +0.303149 | 2 |
| conditions | 1,000 | 9.488218 | 10.137390 | +0.671043 | +0.558590 | 2 |

The large-input cost persists in a modest bounded form: about +0.30ms per
1,000-symbol compilation and +0.56ms per 1,000-condition compilation at paired
median, each slower in five pairs. Paired mean deltas average approximately
0.28 and 0.67 microseconds per source entry. Those averages cover complete
lowering/destruction and are **not** a causal measurement of one operator.
The small-input results remain mixed; the original approximately 0.1–0.2ms
small-case penalty is not stable under this control.

This is an accepted delivery trade-off, not a speedup claim. The richer inferred
capabilities and corrected path comparison/normalization add useful semantics
and safety, while these controls show no heap amplification or expression-layout
growth. The phase and allocation profiles narrow the likely remaining work to
checking, normalization and/or generated-code effects rather than growing
environments or larger expression payloads. They do not distinguish which
specific branch is responsible. A current `perf stat -e task-clock -- true`
capability probe was denied with `perf_event_paranoid=3`; host security settings
were not changed and no sampled CPU profile is claimed. The measured intervals
are wall time for pure compiler work, not direct process-CPU counters.

Root accepted this bounded, documented cost in exchange for the new functionality
and correctness. No speculative cache, weakened checking, further measurement or
production rewrite is justified by this result. All e18 and delivery epochs keep
their own identities and both positive and negative rows; final delivery is
described as modest/mixed compiler cost, never the earlier e18 speedup. Compiler
performance work is closed with no remaining owned build or measurement process.
