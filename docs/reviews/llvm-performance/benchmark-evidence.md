# Independent forensic review: benchmark evidence

## Scope and verdict

Reviewed the final `benchmark-tier-standard.json` (five pairs, seed 29) and
`compile-{numeric,midend}.json` (nine pairs), frozen Corpus runner, product route
ownership, and existing Unicode profiles. No builds, new timings, product edits,
case removal, or gates. Calculations below are offline, using all samples.

**The candidate still has actionable end-user latency regressions. The strongest
current evidence is a composite artifact/runtime cost, not a measured hot-loop
regression or an isolated SROA cost.** Keeping the composite user journey is
correct; the next diagnostic should factor its causes rather than replace it.

Frozen binary SHA-256 independently rechecked against both originals and copies:

- Control: `aaf100d5999642f8566761e86c37b31ed0d71d4f8cf87e5e36f3cb845f175ff6`.
- Candidate: `3c821875c8c954db6f2e1c70c7eb47bc69228e32ad0c572c1453b58f8dceb410`.
- Both payload receipts have the same SQLite library hash. Final Corpus reports
  frozen payloads unchanged. No stale-candidate mismatch found.

## Ranked causal findings

### 1. Confirmed attribution boundary: bound timing is not runtime-only

**Locations:** `tests/Performance/Corpus/run.py:303–330, 357–379, 523–555`;
`src/Msync.Cli/Commands/NativeCommandPipeline.cs:75–89, 130–165`;
`src/Msync.Runtime/NativeModel/Tiering/NativeExecutionImage.cs:501–517`.

Final configuration has `shared_artifact=false`. Each binary compiles and binds
its own source-derived artifact before measurements; timed bound commands run
`baseline.mexe` under baseline and `candidate.mexe` under candidate. All six bound
hashes differ across producers (receipts below). Thus code-generation graph,
artifact decode, interpreter initialization, per-process tier selection and
compilation, guest execution and disposal all contribute. The executable stage
returns early from `Bind` at line 78: **bound runs do not redo contextual
NativePreparation**. They still incur runtime image/tier preparation. Source
runs additionally compile, bind and prepare. Do not lump these two preparation
owners together.

This matters directly to the register-allocation lead: each timed invocation is
a fresh process. Two warmups warm OS pages, not the next process's tier cache.
`TryCompile` performs fresh NativeCompiledFunction compilation when call/backedge
thresholds are crossed. A cost paid once per hot function is therefore part of
every recorded latency, not amortized away by runner warmups.

**Evidence strength:** established route ownership, not estimated attribution.
The +118.479 ms median paired bound Unicode delta can contain runtime setup and
artifact/runtime interaction. It cannot be assigned wholly to projection moves.

**Discriminating experiment:** use the already frozen Unicode artifacts and
binaries in a 2x2 crossover: both binaries run each producer's `.mexe`, identical
cwd/stdin/oracle, explicit `MSYNC_JIT=auto`, telemetry off, two external warmups,
three balanced sequential blocks (12 timed invocations). Interleave cells with
reversed order in alternating blocks. This separates runtime main effect,
producer-artifact main effect, and interaction without compiling anything.
If a binary cannot read the other artifact, record that precise compatibility
fact rather than silently recompiling. Separately profile each valid cell once
at matched detail to assign setup versus execute work. Keep the present final
journey results as the acceptance workload.

### 2. Confirmed shared wasted work, with observed growth not yet timed causally

**Locations:** `src/Msync.Runtime/NativeModel/Tiering/NativeTierLayout.cs:40–43`;
`NativeRegisterPlan.cs:44–55,72–76`; backend emitter records zero raw-writable
slots (`NativeStandardBackendEmitter.cs:47`).

Register planning constructs CFG, whole-function `LiveAfter`, and `PendingSlots`
before finding any eligible region. Eligibility requires a raw-writable result.
Both baseline and final Unicode observations report zero regions/values/spills;
the profiled register-allocation durations are 23.467 / 47.048 ms. This work is
real and recurrent in the fresh-process journey. Zero regions does not make its
analysis free. It is a good first optimization target because it has no resulting
register benefit in this cohort.

**Evidence strength:** unnecessary analysis is established in current code;
growth is observed in one instruction-recording sample per image, not an isolated
23.581 ms telemetry-off regression. Both control and candidate pay the waste.
Do not claim removing it proves the entire compiler cohort's regression fixed.
A product fix should express the no-eligible-result invariant before expensive
analyses, preserving behavior for genuinely raw-writable functions, then compare
candidate before/after on the same frozen artifact. No global JIT disabling or
reduced observability is needed.

### 3. Source-minus-bound and profiled compiler spans do not explain each other

**Locations:** `run.py:303–330, 397–436, 537–555`;
`NativeCommandPipeline.cs:42–52, 75–89,104–108`;
`CommandOptions.cs:28` (source `run` defaults to O2).

Source and bound are measured in separate sequential blocks (bound first), not
matched within a four-invocation block. Source recompiles on every process;
bound executes the once-produced frozen graph. Source may therefore include
fresh compilation variability as well as compiler/binding overhead. The separate
`compile -O2 ... -o output.mso` experiment additionally serializes/overwrites an
object, unlike source `run`'s in-memory compile path. Their differences are not
an exact decomposition identity.

| Workload | Bound median pair delta ms | Source median pair delta ms | Source slower pairs |
|---|---:|---:|---:|
| numeric-record-stream | +5.806 | +12.844 | 3/5 |
| live-policy-dispatch | +7.870 | +25.312 | 4/5 |
| unicode-path-manifest | +118.479 | +231.259 | 4/5 |

Numeric's separate compile paired median is only +0.370 ms (5/9 slower); midend
is +4.738 ms (6/9 slower). The latter is a credible lead for compilation work,
not proof that the independently profiled SROA-minus-mem2reg difference of about
29 ms explains the source-route regressions. Inclusive optimizer/pass spans
must not be summed. Unicode's source overhead is substantially larger in this
sample than these isolated small-source compile deltas; investigate route/setup
ownership instead of attributing it automatically to SROA.

**Practical next step:** first perform the crossover above. If source remains
materially worse after artifact/runtime effects are understood, collect a small
matched-detail source/bound phase comparison on Unicode itself, not the midend
fixture, with fresh per-process tier ownership explicit. Use phase evidence to
choose a product change, then time only that change against the same journey.

## Raw pair review

All Corpus timed blocks start B,C and alternate C,B (seed phase=0). Warmups are
pairs -2 and -1, also B,C then C,B. Every listed array is in pair order, not sorted.
Positive means candidate slower. Percent medians below reproduce the receipts;
median process latency ratios are a different statistic.

| Case/route | Per-pair percentage changes | Median % |
|---|---|---:|
| numeric bound | +5.12, +1.71, -2.22, +2.57, -3.67 | +1.71 |
| numeric source | -0.45, -1.70, +15.98, +3.66, +8.59 | +3.66 |
| policy bound | +1.70, +1.63, +19.41, -1.30, +5.13 | +1.70 |
| policy source | -4.85, +0.38, +7.41, +22.01, +5.01 | +5.01 |
| Unicode bound | -9.87, +0.56, +8.75, +3.97, +6.81 | +3.97 |
| Unicode source | +2.95, +7.69, +35.48, +10.66, -1.50 | +7.69 |

Numeric bound illustrates why the reported p50s are not contradictory:
B=332.394 ms and C=329.553 ms, yet paired median percentage is +1.71%.
Do not replace it with a ratio of those p50s.

The Unicode bound first pair is B=3852.432/C=3472.307 ms; subsequent four have
positive deltas, including +254.475/+118.479/+207.741 ms. This is not just one
slow candidate observation. Policy bound has one +86.009 ms pair but also three
other positive pairs. Numeric bound is mixed and offers much weaker directional
evidence. No sample was discarded, including the first pair.

Nine-pair compile order is B,C for odd-numbered pairs and C,B for even-numbered:

- Numeric delta ms: +0.908, -3.721, -6.214, +5.604, +4.696, +0.370,
  -0.753, -0.580, +3.612.
- Midend delta ms: -3.884, -1.277, +4.738, -14.672, +18.624, +12.567,
  +16.195, +1.520, +5.668.
- Numeric median delta split by B-first / C-first: +0.908 / -0.105 ms.
  Midend: +5.668 / +0.122 ms. With five/four points these are descriptive order
  sensitivities, not a proven order effect. Midend last five pairs are all
  positive, pointing to a time-varying condition or genuine overhead emerging
  as baseline warmed; the present record cannot discriminate those mechanisms.

This evidence supports working on specific setup/compiler causes; it does not
support reporting a precise universal regression percentage or dismissing all
results as noise. No CI timing gate is proposed.

## Identity, environment, and correctness receipts

Bound hashes recorded under each case's `preparation`:

| Case | Baseline `.mexe` SHA-256 | Candidate `.mexe` SHA-256 |
|---|---|---|
| numeric | `9db97e649b2918ed8772ae331e744398523e80ab8c9bfccf4a903b5c2b85dc1a` | `5276515e7e8ae3510d2587d6b8dab22304620d767ac26016f1a9a6f8b1956a60` |
| policy | `9b0faab052d31c49701894959b8bd7d8c1ff65f71a40b32e9f4e41951adf1c48` | `275bdd0fcb64651c1ec95f1cbeee02f38ce2d771fbd45ddd13bf9a8930fcdeac` |
| Unicode | `be70c63aad546cebab3bd42de767ad94a6c9576355b4a1fa94db74b252ea2471` | `6168cbcb6326811ea2b61a70accb306e73b326fb23f24f28b29616776f8914d3` |

- Corpus: Linux 6.12.113+, Python 3.15.2, available CPU affinity 0–15. Affinity
  is recorded availability, not single-core pinning or proof of idle host.
  `invoke` inherits the environment (`run.py:145–146`), adds `--no-config` and
  `--no-telemetry`, and does not freeze/record `MSYNC_JIT`. Therefore the exact
  policy is not established by the final timing JSON alone. Product default is
  Auto with call=32/backedge=128 (`NativeTierOptions.cs:5–15`); `MSYNC_JIT`
  can select Off/Force/Linked even when config is off. Existing diagnostic tier
  observations establish active tier work for those observations, not a hidden
  environment snapshot for the telemetry-off cohort. Record this variable in
  the proposed experiment rather than claiming the current run used Off.
- Compile: explicit `--no-config --no-telemetry compile SOURCE -O2 -o PATH`;
  same existing output overwritten, hash outside timer; one untimed launch per
  image; warm file cache, no cache flush; `NO_COLOR=1`. No retained runner file
  was found for this separate inline experiment, so the JSON is the primary
  order/command receipt. The compile artifact hashes/byte sizes change across
  repeated same-image launches; this is not evidence of semantic failure, but
  means repeated compiles are not byte-identical artifact controls. Use the
  frozen Corpus artifact for runtime-causal experiments.
- Timer surrounds fresh subprocess launch through completion and captured pipe
  I/O (`run.py:142–160`). Fixture reset happens before the timer, independent
  verification after it (`run.py:186–203`). No oracle computation is included
  in product elapsed time. The selected three cases have stdout oracles, not
  file-transfer output trees, so output-file reset/copy work does not explain
  their reported elapsed deltas.
- Numeric independent integer oracle: `compute.py:248–265`; live-policy
  independently formulated weighted-average recurrence: `compute.py:136–159`;
  Unicode Python POSIX lexical/text/UTF-8 oracle: `text_paths.py:114–151`.
  Both variants match the same oracle each invocation, not each other's output.
  These checks meaningfully cover captures, policy state and Unicode/path
  accounting. Separate detached-route receipts cover source removal and
  artifact-only execution; compile output hashes alone do not validate behavior.

## Excluded conclusions

No current evidence reopens repaired constant reuse, PHI carrier selection,
capture schema, or FunctionComponent fallback handling. Equal opcode counts
and zero register spills do not establish equal host instruction or allocation
cost. NativeDatum/NativeValue are structs; no heap allocation is inferred from
their presence. The 7.300 MB execute-allocation difference in the existing
Unicode profiles is aggregate evidence, not allocation-site attribution.
