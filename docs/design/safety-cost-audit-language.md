# Safety-cost audit: language, compiler and executable objects

## Implemented normal-user follow-up (2026-11-01)

The user explicitly authorized removing the defensive acceptance policies after
this audit. The implementation now removes the false 128-sibling call cap,
source-import per-file/aggregate byte and occurrence quotas, source-selector
width cap, symbol invocation/value quotas, object wire/instruction quotas and
source-module binding cap. Symbol calls are still counted for diagnostics, but
the counter is not an admission budget. The public object magic, feature/type/
reference semantics and numeric ID representation are unchanged.

Quoted native paths are no longer rejected because a slash/backslash-separated
component begins or ends in whitespace. Quoting already distinguishes literal
filename characters from source-layout whitespace; actual nonempty/NUL path
requirements remain. Acceptance includes real Unix copies of leading/trailing
space names and a name containing both newline and literal backslash. Bare
quoted transfer sources retain their existing literal-data meaning.

Object construction now adds source-module bindings before one full validation.
An immutable `LoadedModule` carries a decoded/compiler-produced object through
encoding and contextualization without repeating whole-module or debug-span
verification. The old public mutable object APIs remain available and interpret
their supplied data at the ordinary API boundary. This is an immutable data path,
not a mutable validation cache. Module reachability/closed dependencies use
indexed worklists rather than repeated full-table scans or iteration quotas.

Compiler Scope composition no longer recursively measures an accumulated chain
and rejects it at 128. Composition is associative application: flatten its leaves
iteratively in original outer/inner order, then build a balanced binary value.
This keeps existing wire/API variants and logarithmic clone/conversion/evaluation
depth without a composition-width quota. Acceptance covers 257 incremental Scope
compositions and grouping equivalence for module/PWD, PATH, WSL and physical TCP
namespaces, including equivalent error behavior for conflicting endpoints.

True recursive implementation-depth bounds remain in parser blocks/ASTs,
active source calls, inline symbol calls, external object visitors and nested range
cursors. Removing those alone would leave recursive clone/drop/evaluation able
to overflow the native stack. They are implementation limitations to replace
with iterative representations, not byte/count quotas or a security sandbox.

Focused acceptance is maintained in `tests/compiler_acceptance_limits.rs`:
actual CLI checking of 129/257 sibling calls, 1,025 repeated includes, more than
32 MiB included input, and 1,025 selected source modules; loaded-object encode/
contextualize round trips; more than 64 MiB valid object framing; and 100,001
typed instructions. Updated symbol cases accept >8 MiB paths/identity payloads
rather than treating them as quota failures. Existing object/schema tests retain
their actual semantic checks.

The integrated selected lane passed all 13 cases: seven compiler acceptance
journeys, two Scope grouping/balancing cases, the acyclic call counter and two
large symbol-value cases, and the existing external object-reference mutation
case. The latter confirms that removing quotas/repeated checks did not remove
actual executable-reference interpretation. Formatting and scoped diff checks
also pass. The broader repository owner performs the final strict static check;
no all-target regression was required for this change.

One discriminating generated workload also exposed a separate planner scaling
problem: 1,025 included modules each containing
`"value" -> $file::parent/result-N` made an actual debug CLI `check` exceed
60 seconds and approximately 13 GiB RSS during observation. This is not an
accepted timing benchmark or proof of its exact cause. The quota acceptance
fixture uses 1,025 `$file::this` value bindings instead, isolating source/object
width from effect-graph construction. No width cap was reinstated. Recreate the
expensive shape using a `parts` directory, one distinct `result-N` transfer per
file, and `include parts/<.+\\.msync>`; investigate graph/resource identity
construction independently rather than explaining the cost as a reason to
reject valid source imports.

## Decision

Remove the parser's **flat sibling-call count presented as recursion depth**.
Replace source-import and artifact-size quotas that currently reject ordinary
single-user programs without a demonstrated workload-derived threshold. Keep
semantic correctness at the actual external artifact boundary, but stop paying
for the same whole-module verification repeatedly inside one immutable operation.
None of this requires a sandbox, signatures, a new authority service, or treating
user-authored scripts as hostile.

This audit covers `language`, `syntax`, `compiler`, `hir`, `object`, `relocatable`,
and object contextualization/IR adapters. The relevant established contracts are
[typed objects](object-context-v5.md), [type inference](type-system-v065.md),
[language](language-v6.md), and [relocation acceptance](relocatable-validation.md).
Storage publication, transport ownership and editor/release policies are separate
audit domains. Source locations below identify functions as well as line numbers
so subsequent edits do not erase the rationale.

The working application model is a single-user synchronization tool: the user
chooses their scripts, files and endpoints, and intentionally launches programs
with their existing OS rights. There is no supported least-privilege execution
sandbox or multi-tenant compiler service. Ordinary mistakes, malformed old
artifacts and accidental explosive expansion are real failure modes; an
unspecified attacker does not justify arbitrary restrictions on normal inputs.

## Actual CLI counterexamples

The probe used the frozen v0.6.5 executable, SHA-256
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`,
and `msync --no-config check FILE`. These are check-only compiler journeys, not
network or runtime benchmarks. Six invocations completed in one short local
probe. Disposable sources and output are under
`.temp/safety-cost-audit-language/{probe.py,results.json}`.

| Valid input shape | Control | Counterexample | Observed result / implication |
| --- | --- | --- | --- |
| Flat literal range of `id("a")` calls, with `symbol id(value): return value` | 128 sibling calls | 129 sibling calls | 128 succeeds; 129 fails with `call expression nesting is too deep`. Nesting is unchanged. This is a real false positive. |
| Repeated `include "empty.msync"`, whose content is one comment | 1,024 includes | 1,025 includes | 1,024 succeeds; 1,025 fails with the combined `64-level/1024-file bound`, although only one file and no executable instructions are involved. The counter measures include occurrences, not distinct files or active depth. |
| One comment containing `8 * 1024 * 1024` letters, plus `#` and newline | Same bytes as top-level source | Same bytes as an included source | Top-level succeeds; include fails with `source file exceeds the 8MiB input bound`. Wrapping unchanged inert content in a module changes acceptance. |

The decisive parser mechanism is
`language/parser.rs::scan`, around lines 1115–1127: when closing an outermost
call/group, it rejects when `result.calls.len() == MAX_NESTING`. `split_arguments`
rescans a range's comma-separated contents, so 129 **siblings** hit this counter.
The independent `delimiters.len()` test around line 1089 measures real nesting
and does not need to be removed to fix the false positive.

Minimal reproduction, using any small deterministic generator rather than
retaining an 8 MiB fixture in version control:

1. Write `symbol id(value):\n    return value\n` followed by a literal range
   containing 129 copies of `id("a")`, separated by commas, then ` -> out/`.
2. Write one comment-only `empty.msync`; write 1,025 copies of
   `include "empty.msync"\n` into an entry source.
3. Write `#`, 8,388,608 ASCII `x` bytes, and newline to `large-child.msync`.
   Check that file directly; then check a small entry containing
   `include "large-child.msync"`.
4. Invoke the same executable with `--no-config check` on every source. No data
   source or destination needs to exist. Compare exit status and the named
   diagnostic, not a time threshold.

## Item-by-item evaluation

Dispositions are **REMOVE**, **SIMPLIFY**, **REPLACE**, or **KEEP**. They concern
this mechanism, not a blanket claim that all checking is either good or bad.

### Parsing, expansion and source intake

| ID / code owner | Mechanism and concrete purpose | Normal-user cost / evidence | Disposition and replacement |
| --- | --- | --- | --- |
| L01 — `language/parser.rs::scan`, ~1121 | At most 128 outermost call/range pairs in one scanner pass, described as nesting protection. | Rejects the demonstrated 129-element flat range; no recursion-depth benefit. | **REMOVE, P1.** Delete the sibling-pair quota; retain actual delimiter-stack depth accounting. Add the public check/compile journey as the regression, not a new numeric limit. |
| L02 — parser `MAX_NESTING`, `child`, `expression_with_role`, `scan` | Limits nested blocks, delimiters, unary operators and recursively constructed expression trees to 128. | Guards the implementation's native stack against accidental deeply generated source. A flat boolean/path operator chain is also represented recursively, so an operator count is not identical to source nesting. The value 128 has no repository workload calibration. | **REPLACE, P2.** Separate flat sequence width from true recursive depth. Use iterative parsing/traversal for flat chains; keep a diagnosed true-depth bound until the recursive implementation is removed. Do not just delete all stack protection. |
| L03 — `compiler/structured.rs::compose_scopes`, ~2185 | Recursively computes scope-tree depth and rejects composition at 128. | Adds a complete depth walk to each new composition; flat left-associated construction can accumulate quadratic depth-observation work. No external attacker is needed for a generated composition chain. | **SIMPLIFY, P2.** Store depth once or flatten compatible scope composition; use the same true-depth policy as parser/object handling instead of separate magic numbers. No new user-facing knob is needed merely to remove repeated tree walks. |
| L04 — `compiler/structured/scripts.rs::{FILE_BYTES,TOTAL_BYTES,FILE_COUNT,DEPTH}` and `script_file`, ~127–166 | 8 MiB per included file, 32 MiB cumulative includes, 1,024 include occurrences, active depth 64. Claimed protection is bounded expansion work. | Two demonstrated acceptance discontinuities above. Re-including an inert file spends both byte and file quotas repeatedly; top-level source is not subject to the per-import cap. Quotas cannot distinguish useful generated input from explosive expansion. | **REPLACE, P1/P2.** Remove the fixed occurrence/byte ceilings as an unconditional language contract; retain actual active-chain cycle detection. If evidence warrants a compiler resource budget, make it one explicit compilation policy tied to expanded work, not four unrelated hidden rejection thresholds. A per-compilation source/parse cache can avoid rereading and reparsing identical inputs without changing repeated-include semantics. |
| L05 — scripts `script_matches`, ~352–364 | Stops a source selector after 1,024 matching entries. | Blocks a perfectly finite generated module directory independently of nesting; repeats L04's width quota. | **REMOVE with L04, P2.** Enumerate the selected finite set normally, keep deterministic name ordering, and charge any justified shared resource policy rather than a second local cap. |
| L06 — scripts `read_script`, ~414–433 | Checks initial metadata size, then reads at most limit+1 and checks actual bytes. | The double check is sensible *only if* a byte limit is actually part of the intake policy: a file may grow after metadata observation. Removing the policy while leaving both checks would retain pointless machinery. | **SIMPLIFY with L04.** Keep the bounded-read pattern only where an explicit real input budget requires it. Do not describe the metadata precheck as independent security value. |
| L07 — scripts `active: Vec<same_file::Handle>`, `script_file`, ~139 | Detects active include cycles by actual file identity, including aliases/hardlinks; includes root identity. | One open identity capability per active file. It prevents accidental unbounded compiler recursion; ordinary source symlinks are **not** blanket-banned here. | **KEEP.** This is a precise failure model and a simple ownership-local check. Keep repeated, non-active inclusion legal; do not turn it into global include-once behavior. |
| L08 — scripts `open_source`, ~398–411 | Opens Unix compiler sources with `O_NONBLOCK|O_NOCTTY`, then requires an ordinary file. | Prevents an accidental source FIFO/device from taking over or indefinitely waiting in a compile-input loader; unlike top-level input, imports do not expose an explicit stream intake contract. It also excludes deliberate named-pipe source generation. | **KEEP current file-input boundary; REPLACE if stream imports become a supported journey.** Then accept an explicit source-provider/EOF stream abstraction rather than a permission or sandbox exception. This is an implementation/input-kind contract, not proof that local files are trusted and streams hostile. |
| L09 — scripts `script_path`, ~261–282 | Only Local/Env/PATH backends may supply compile-time source; remote input says to make a local source copy. | Stops an explicit remote include workflow. Current compilation deliberately has no implicit remote authentication/effects, and the compiler requires actual source bytes, not a future runtime filename. | **KEEP phase ownership; SIMPLIFY UX.** Do not call this a security boundary. An explicit source-acquisition layer could support remote module inputs without turning every possible branch reference into network admission; adding generic background network access to the compiler is not a justified fix. |
| L10 — scripts UTF-8 path/name conversion and silent skip of non-UTF-8 selector entries, ~354 and ~381 | Source identity and language names are stored as strings. | Existing Unix filename bytes outside UTF-8 can be omitted from matching source selection. This is a representation limitation, not an attack defense. | **REPLACE, P2.** Retain `PathBuf`/`OsString` for physical source identity, derive a lossless display label separately, and report a selected unrepresentable source rather than silently omitting it. Do not fabricate a security rationale. |
| L11 — `symbols.rs::{DEPTH_LIMIT,INVOCATION_LIMIT,VALUE_LIMIT}`, lines 7–37 | 128 active symbol calls, one million calls per compilation, and 8 MiB path/string results during symbol expansion. | Active recursion and exponential value growth can exhaust a user's compiler/REPL without malicious input. Existing `symbol_path_doubling_stops_before_allocating_oversized_join` proves the projected-growth guard acts before the large allocation. But `identity(large_literal)` fails while the same direct payload works; raw call count does not measure allocation/work fairly. | **REPLACE budget model, P2; KEEP preallocation overflow/growth reasoning.** Avoid copying/expanding unchanged values first; account for actual materialized work. If a budget remains, expose one compilation resource policy with useful consumption diagnostics. Do not introduce a general payload-size limit. |
| L12 — symbol/macro active recursion rejection, `symbols::definition` and `Lower::recursion`, ~1344 | Lexical functions/macros inline at compile time; active recursive expansion cannot terminate by an ordinary runtime return. | Rules out recursive user functions, an explicit missing language capability. It avoids accidental infinite inlining. | **KEEP current inlining contract.** Runtime recursion needs a call-frame model; removing this check would not implement that feature and would only crash/hang compilation. This is not sandboxing. |
| L13 — `symbols::validate_body`, ~204–239 | Pure functions allow pure bindings/imports/nested declarations/return, require a return, and reject post-return statements. | Cannot hide transfers or program launches inside a supposedly pure expression; unreachable statements are rejected instead of ignored. | **KEEP purity and required result; SIMPLIFY unreachable-code policy if user demand arises.** Purity enables correct value inference and scheduling without effects, not arbitrary restriction of user authority. Post-return code could be a diagnostic rather than fatal, but there is no measured bottleneck or supplied blocked workflow yet. |

### External artifacts, semantic invariants and repeated checking

| ID / code owner | Mechanism and concrete purpose | Normal-user cost / evidence | Disposition and replacement |
| --- | --- | --- | --- |
| L14 — `object.rs::{MAX_OBJECT_BYTES,encode,decode}`, lines 23, 307–335; `main.rs::read_content`, ~776 | 64 MiB object wire cap; reads identified objects only up to cap+1; source input remains uncapped. | Rejects the compiler's own fully constructed encoded output as well as loaded input. Objects embed complete debug source and pretty-printed JSON, so size is not proportional only to executable work. No demonstrated 64 MiB user-workload threshold or hosted compiler attack model is recorded. | **REPLACE, P2.** Separate wire representation from a real allocation policy. Do not apply an intake-defense cap to already-owned compiler output. Stream encoding/loading where useful; a versioned compact/debug-separated format is a design option, not a reason to reject current normal programs. Preserve existing wire compatibility while broadening acceptance. |
| L15 — object validator `MAX_OPERATIONS`, `ProgramState::region`, ~13 and 179 | 100,000 instructions, independently of 64 MiB wire cap and actual executor capacity. IDs separately bounded to `u32`. | A large finite generated source can compile in-memory but fail object creation. The combined diagnostic conflates size, duplicate identities and invalid numeric range. | **REMOVE arbitrary instruction-count ceiling or REPLACE with explicit shared resource policy, P2.** Keep unique IDs and portable numeric representation. Report each failing invariant separately; do not relax duplicates merely to permit more instructions. |
| L16 — object recursive region/expression/scope depth 128, `validate.rs::region`, `validate_control.rs::expression`, `validate_namespace` | Bounds recursive visitors and interpreter value graphs. | Real native-stack implementation concern; codec has an additional independent nesting limit. Same source/object program can hit limits at different structural levels. | **REPLACE implementation dependence, P2.** Use iterative graph/tree work where feasible and one specified depth semantics. Keep bounded recursive decoding until all downstream recursive clone/drop/visit operations can handle deeper structures. No blind `serde_json` unbounded-depth toggle. |
| L17 — `validate_control.rs::range`, ~398–401 | Rejects flat range adaptor pipelines longer than 128; current cursor implementation nests upstream cursors. | This is flat width in the language but recursive runtime machinery. Existing `object_v6::forged_flat_pipeline_cannot_build_an_unbounded_cursor_chain` records the concern, not a calibrated safe ceiling. | **REPLACE, P2.** Represent the adaptor chain iteratively or flatten compatible stages; then remove the exposed stage-count rejection. Removing the check without fixing recursive cursor construction is not a complete delivery. |
| L18 — `ObjectModule::from_parts` → `with_source_modules` → `encode`; `decode` → `context::contextualize`; `Structured::contextualize` | Re-verifies a whole unchanged public module at successive internal calls. Construction+encoding performs three module passes; ordinary object load+contextualization performs two, and `debug.materialize` separately re-verifies debug metadata. Structured IR validates before calling another validating context entry. | Each module validation walks instructions/expressions/relocations/source maps and re-derives symbol/import/constraint tables. No wall-time regression percentage is claimed here, but repeated traversal is directly present in the actual call graph. Public `ObjectModule` fields are mutable, so simply deleting public-entry validation would break the current guarantee. | **SIMPLIFY, P1/P2.** Keep validation at public mutable/wire boundaries; pass an internal immutable `ValidatedModule<'a>` proof between decode, bind and encode steps. Mutation/new transformed IR invalidates that proof. Preserve old public APIs; do not add a mutable cached `validated` boolean. Compiler-owned `contextualize_source` already shows the intended separate path. |
| L19 — `validate.rs::module`, ~15–66; serde `deny_unknown_fields` | Rejects unsupported format/language/features and unknown executable fields rather than silently reinterpreting them. | Older tools cannot run truly new operations. This is semantic compatibility, not authentication. Current exact feature-set check also rejects unknown optional metadata, but no current extension producer requires it. | **KEEP executable feature negotiation and semantic schema.** If optional metadata is needed, give it an explicit ignorable extension representation; do not silently ignore new executable semantics. Preserve working old feature aliases and contracts. |
| L20 — relocation validation/field clearing, `object.rs::from_parts`, `validate.rs::module`, ~74–92 and `operand` | Relocated physical paths must be empty placeholders with one compatible valid relocation; references must exist. | Prevents an omitted/stale relocation from unexpectedly targeting the compiler machine's old absolute path on another user's machine. This accidental wrong-target model is meaningful even for wholly trusted authors. | **KEEP invariant, SIMPLIFY representation over time.** Represent bound and relocated operands as distinct variants internally so impossible mixed states cannot be constructed. Do not reinterpret debug source to repair missing executable intent. |
| L21 — type/slot/process/port validation, `validate_control.rs`; `validate.rs::instruction/consume` | Checks operation result types, defined slots, valid process definitions, unique identities, compatible endpoints and one consumer/producer per linear stream. | Without these, own stale/edited objects can mis-execute or block rather than yield a useful diagnostic. User programs still launch at normal OS rights; validator is not a sandbox. | **KEEP external semantic verification; centralize typed invariants.** Share operator capabilities with source lowering, and use typed validated runtime IDs inside execution rather than repeated untyped probing. |
| L22 — loop capture checks, compiler `v6.rs::loop_body`, ~745–752; object `validate_control.rs::loop_body` | Rejects re-consuming an outer non-replayable program stream on every loop iteration. | Blocks an intuitive but ambiguous repeated-read workflow; otherwise iteration two observes EOF or races the first consumer. Actual file snapshots are replayable; live streams are not. | **KEEP ownership semantics.** Support deliberate capture/replay as an explicit value/operation rather than allowing silent duplicated stream consumption. Do not justify this as malicious-handle defense. |
| L23 — derived `symbols/imports/constraints` consistency, `validate.rs::module`, ~107–113 | Serialized derived tables must agree with authoritative typed instructions. | Reconstructs tables during each validation, storing redundant wire data and allocating sets/maps. Needed because old/public wire exposes both representations, not because duplicate data is inherently safer. | **SIMPLIFY, P2.** Derive once at load, use the validated derived instance, and ignore non-authoritative optimization hints for execution authority. Future format may omit redundant tables, but keep decoding current objects and do not change their executable semantics. |
| L24 — source-module bindings, `object.rs::with_source_modules`; `validate.rs::source_modules`, ~720–777; `context::source_module_context` | Caps bindings at 1,024 and repeatedly scans to prove dependency resolution/acyclicity. | Another independent module-width cap and worst-case repeated scans, while valid contextual dependency graphs merely need a topological traversal. Conflicting logical module anchors can bind different files after relocation, so their conflict check is meaningful. | **REMOVE hard binding-count cap with L04; SIMPLIFY dependency resolution.** Use one indexed dependency graph/topological order, retaining actual cycle/unresolved/conflicting-anchor diagnostics. No new authority registry is needed. |
| L25 — debug `validate`, exact origin coverage and `materialize`, `object/debug.rs:95–151`; `validate.rs::debug`, ~362 | Validates source table indices, UTF-8 intervals, unique origin/export sites and exact operation origins. Then validates again before unchecked indexing during materialization. | Protects meaningful error attribution and prevents own corrupt source maps from indexing outside strings. Complete debug information also enlarges objects and can make debug-only damage fatal to otherwise valid executable IR. | **SIMPLIFY repeated validation, P2.** Carry a verified debug view into materialization. Keep current source-owned diagnostics; if stripped/optional debug objects become useful, design that representation explicitly instead of making arbitrary bad metadata silently valid. Rejecting bad spans once is justified; traversing unchanged spans again is not. |

### Basic semantic constraints which are not security mechanisms

| ID / code owner | Mechanism / value | Decision |
| --- | --- | --- |
| L26 — compiler `resolve_path`, `resolve_join`, `qualify`, object endpoint/path checks | NUL cannot be represented by native argv/path APIs; endpoint conflicts and rooted RHS replacement would discard a different path/endpoint than the user wrote; a known file value does not have child components. | **KEEP.** These express actual operator/platform semantics. A string literal used as file bytes remains distinct from a path. Improve types, not a `trusted` bypass that changes join meaning. |
| L27 — `CompileContext::validate`, `context::validate_context` | Context paths are absolute logical snapshots; no canonicalization or file existence is required at compile time. | **KEEP basic context invariant; SIMPLIFY duplicate checking within an immutable call chain.** This does not prohibit symlink paths or require pre-existing future outputs. Do not add filesystem probes for reassurance. |
| L28 — candidate compiler/session state, `Session::compile_named` and `contextualize` | Compile/import into a candidate, commit only on complete success. Persistent maps share lexical state; fresh IDs avoid collision. | **KEEP.** A failed REPL definition must not half-mutate the next submission. This is user-state consistency, not adversarial isolation. Optimize sharing rather than exposing partial compiler state. |
| L29 — `language/version.rs::select_version`, ~43–95 | Rejects unsupported grammar headers, contradictory/late headers instead of interpreting quoted bytes or transactions under another grammar. | **KEEP compatibility semantics.** A warning-and-guess fallback could change file contents/effects. Grammar alias support remains preferable to gratuitous rejection where meaning is actually compatible. |
| L30 — `compiler/structured/diagnostics.rs::{FRAME_LIMIT,VALUE_LIMIT}` | At most 16 expansion-provenance frames and 160-character value summaries, with explicit truncation. It does not truncate executable values. | **KEEP display budgeting; SIMPLIFY observability if needed.** Long paths and expansion origins must remain retrievable through full source/structured diagnostics. Never reuse display quotas to reject actual input. |

## Concrete implementation order

1. Remove L01's false depth check and test the same flat range through parse,
   compile and object encode/decode. Keep the actual nested-delimiter control.
2. Replace import counters/size refusals (L04/L05/L06/L24) coherently; do not just
   raise 1,024 to another arbitrary number. Preserve active identity cycle checks
   and repeated-include lexical semantics. Exercise a generated many-module source
   and re-inclusion of one module with no runtime effects.
3. Introduce an internal immutable validation proof for the existing object wire
   type (L18/L25); compare actual source/object journeys before claiming a latency
   improvement. Public mutable `ObjectModule::validate/contextualize/encode` must
   keep working, including callers that deliberately mutate a previously decoded
   object.
4. Make width independent of recursive implementation shape for parser chains,
   scope composition and range cursors. Revisit true-depth limits only after
   clone, visitor and drop paths are also safe for deeper trees.
5. Reassess object encode/decode resource policy with a real large generated
   module and memory/allocation observations, especially embedded debug source.
   No security claim justifies its current 64 MiB or 100,000-operation constants.

There is **no object signature, MAC, cryptographic content checksum, import
allowlist, source-directory sandbox, or broad source-symlink ban** in this domain.
Do not invent one while simplifying validation. Trusted user intent and valid
machine representation are different issues; a corrupted file can still be a
normal user's file and deserves a useful diagnostic rather than a crash.

## Relevant production and research mechanisms

- [Rust's compiler limits](https://doc.rust-lang.org/reference/attributes/limits.html)
  distinguish recursion depth and type-substitution work, with explicit control.
  This supports separating resource shape from arbitrary flat input width; it
  does not validate copying rustc's default 128 into every msync data structure.
- [serde_json deserializer documentation](https://docs.rs/serde_json/latest/serde_json/struct.Deserializer.html#method.disable_recursion_limit)
  explicitly warns that removing decoding depth protection leaves recursive
  display/debug/drop operations exposed. Therefore L16 is an implementation
  replacement, not permission to toggle unbounded decoding blindly.
- [MLIR's developer guide](https://mlir.llvm.org/getting_started/DeveloperGuide/)
  recommends moving repeated invariants into verification or redesigning the IR
  to express the phase invariant. The applicable lesson is one verified
  representation, not a second verifier at every innocent handoff.
- [Strom and Yemini, *Typestate* (1986)](https://www.cs.cmu.edu/~aldrich/papers/classic/tse12-typestate.pdf)
  motivates representing semantic state transitions in types. A borrowed internal
  `ValidatedModule<'a>` is a small practical application: verification remains at
  the boundary, and unchanged internal callers carry its result without redoing
  its work. This is a design proposal, not a measured speedup or a claim of formal
  verification of msync.
