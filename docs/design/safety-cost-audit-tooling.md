# Safety-cost audit: configuration, diagnostics, editor and distribution

## Decision and scope

The inventory records the pre-removal baseline. The user's subsequent explicit
normal-user/retry contract supersedes earlier recommendations to retain
admission policies; the implemented follow-up at the end states current behavior.

The highest-value corrections in this domain are not weaker credential handling.
They are removing the authority of optional telemetry over transfer outcomes,
validating the effective configuration once rather than rejecting overridden or
inactive settings, and replacing editor/formatter blanket refusals with ordinary
supported workflows. No production implementation was changed by this audit.

This is a single-user synchronization application running the user's chosen
scripts and endpoints, not a sandbox. Malformed protocol messages, mistakes,
unavailable storage and interrupted installers are concrete operational failures.
They do not justify assuming every local filename, configuration value or editor
clipboard is an adversarial capability. A restriction needs a user-value argument,
not merely a comment containing “security” or “untrusted”.

The inventory covers `config`, CLI/REPL/editor/LSP, telemetry, doctor,
presentation, askpass/authentication UI, packaging and workflows. Transport,
publication and language/object internals have separate domain audits. Locations
below identify symbols and current source line ranges; symbols remain the primary
anchors as the SSH patch changes nearby files.

Disposition vocabulary: **REMOVE** an unjustified restriction; **SIMPLIFY** retain
the outcome with fewer checks/owners; **REPLACE** change the mechanism; **KEEP**
retain a concretely useful boundary. Priorities concern normal user impact, not
an invented attacker severity score.

## Reproduced delivery problems

These are actual CLI journeys against the immutable v0.6.5 delivery executable
SHA-256 `f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`.
Each case runs a local `"published bytes" -> target` statement in a private
directory under repository `.temp`; no network, host configuration or existing
user file is touched. Probe/results are disposable under
`.temp/safety-cost-audit/tooling`.

| Case | Observed outcome | User-value conclusion |
| --- | --- | --- |
| `[telemetry] enabled=false; queue_capacity=0`, even with `--no-telemetry` | Exit 1, no target, SQLite queue error | A disabled observer prevents unrelated file work. |
| JSONL telemetry with an irrelevant `queue_capacity=0` | Exit 1, no target, SQLite queue error | Validation ignores selected backend. |
| `[execution] jobs=0`, overridden by CLI `--jobs 4` | Exit 1, no target, original jobs error | Lower-precedence validation defeats documented override precedence. |
| `[progress] delay_ms=86400001` | Exit 1, no target, 24-hour-limit error | An arbitrary UI duration cap becomes an execution gate. |
| SQLite telemetry `max_records_per_session=1` | Target contains exact 15 bytes; stdout says `done`; process exits 1 because telemetry cap was reached | Automation sees failure after successful publication and may retry already completed effects. |
| Optional SQLite destination in a missing parent | Exit 1, no target | Exporter admission, not transfer capability, decides whether the user's operation can run. |
| `fmt` through an ordinary symlink to a writable script | Exit 1, explicit symlink refusal | User-selected indirection is unsupported even without a conflicting writer or attack. |

The reproduction procedure is simply create those small TOML files next to the
copy script, then run `msync --config CONFIG run copy.msync` with the indicated
override. For the cap case, check both exit status **and actual target bytes**;
unit tests of the exporter alone would miss the user-visible outcome coupling.

## Configuration inventory

| ID / priority | Mechanism and source | Concrete benefit or failure model | Normal-user cost | Disposition and simpler design |
| --- | --- | --- | --- | --- |
| C01 / P1 | `Loaded::apply` calls `validate_values` before CLI overlays (`src/config.rs:365–481`); CLI validates again (`src/main.rs:465–516`) | No additional benefit from rejecting a value which will not be used | Proven `jobs=0 --jobs 4` failure; duplicate policy path | **SIMPLIFY:** deserialize, apply all precedence layers, then validate the effective settings once. Retain source-span provenance from the winning value. |
| C02 / P1 | Unconditional `telemetry.sqlite_options().validate_keyed()` (`config.rs:535–547`) | SQLite needs valid options only when SQLite will open | Proven disabled/JSONL failure; `--no-telemetry` cannot repair it | **REMOVE** inactive-backend validation; keep representable TOML types, validate actual backend admission. |
| C03 / P2 | Progress delay/interval maximum 86,400,000 ms (`config.rs:549–558`) | No measured resource or timer limitation establishes a 24-hour maximum | Proven ordinary copy refusal over a UI preference | **REMOVE** arbitrary maximum. Handle timer representability locally; a very large delay naturally means no progress display during a short job. |
| C04 / P3 | Config must be a regular file and at most 1 MiB (`config.rs:17–36,305–329`) | Conventional discovered config must not accidentally hang on a FIFO; bounded TOML parsing uses finite resources | Explicit process-substitution/FIFO config and large generated config rejected; no workload evidence for exactly 1 MiB | **REPLACE** blanket input policy with discovered-file versus explicit-input semantics if stream input is offered. Keep a default read budget, but do not describe 1 MiB as an established threat boundary. |
| C05 / P3 | Pre-open metadata check plus post-open metadata check, Unix `O_NONBLOCK` (`config.rs:19–36,308–317`) | The opened handle is the actual object; nonblocking open avoids a FIFO race hang | Two metadata queries for ordinary config startup | **SIMPLIFY:** on Unix open nonblocking and classify the actual handle once. Do not duplicate a pathname classification for “extra safety”. Preserve Windows-specific launch semantics. |
| C06 / keep | `deny_unknown_fields` on settings and typed values (`config.rs:825–874`) | Misspelled `state_dir`, `jobs`, or telemetry options otherwise silently do something else | A newer setting in an older binary requires repair; syntactically invalid values cannot be overridden | **KEEP** useful typo/type diagnostics. Separate parsing validity from active policy validation (C01/C02); do not silently fall back to another file after a corrupt selected config. |
| C07 / keep | One user config, no implicit cwd config; explicit precedence and relative-path anchoring (`Loaded::load`, `conventional_paths`, configuration-v6.md) | Cwd changes should not silently change where recovery or telemetry is stored | No automatic project config | **KEEP** current explicit configuration contract; this is predictable state ownership, not a sandbox. A future explicit project-config feature can be designed independently. |
| C08 / keep | Help/version bypass config; `--no-config` bypasses discovery (`src/main.rs::run`, `config::Loaded::load`) | Repair commands remain available with broken config | No material normal-path cost | **KEEP**. Extend the same repair principle to inactive telemetry rather than adding another fail-before-work gate. |

## Telemetry and diagnostic inventory

| ID / priority | Mechanism and source | Concrete benefit or failure model | Normal-user cost | Disposition and simpler design |
| --- | --- | --- | --- | --- |
| T01 / P1 | Exporter shutdown error replaces successful dispatch result (`src/main.rs:607–621`) | Makes incomplete recording visible | Proven successful transfer exits 1; callers cannot distinguish failed data effect from failed observation | **REPLACE:** preserve operation exit outcome; issue a distinct, detailed exporter warning/status. A telemetry query/export command still fails when its own requested output cannot be delivered. If strict capture is needed, make that an explicit diagnostic-run contract, not the default transfer result. |
| T02 / P1 | Telemetry init and conventional-directory errors abort before dispatch (`main.rs:551–587`) | Avoids starting without a requested trace | Unavailable optional observer disables a working sync path | **REPLACE:** report attempted path and OS cause, disable only the failed observer, continue the requested work. Do not silently choose another database or pretend capture succeeded. |
| T03 / P1 | `sync_channel` and blocking `send` while holding session sink mutex (`src/telemetry/sqlite.rs:122,152–161`; `src/telemetry.rs:262–269`) | Preserves every accepted record and bounds queued memory | Slow/locked SQLite applies backpressure to interpreter and all emitting threads; instrumentation changes the measured workload | **REPLACE:** bounded nonblocking ingestion with explicit lost-record/gap counters and recording status, or an explicitly selected exhaustive diagnostic mode. Keep errors visible. No producer-to-worker cyclic dependency was demonstrated; do not claim a deadlock merely from blocking. |
| T04 / P1 | Record cap becomes permanent exporter error (`sqlite.rs:366–375`); fixed database cap can stop recording (`page_limit`) | Prevents unlimited observer storage growth | Proven cap converts success into process failure through T01; long REPL sessions eventually hit cap | **REPLACE:** cap/rotate or mark capture incomplete without changing file outcome. Retention/storage budgets remain useful resource policy, not execution admission. |
| T05 / P2 | Internal JSON serialization, queueing of String, JSON parsing and `Record::validate` (`telemetry.rs::event_record`, `sqlite.rs:376–378,587–601`) | Reuses old JSONL API and checks internal wire shape | Every SQLite record pays encoding/decoding and revalidates a shape produced by the same process | **SIMPLIFY:** one typed record model, serialize only for JSONL/export; SQLite consumes the typed record. Keep schema/identity checks at the persisted database boundary, not repeated trusted interior conversions. No wall-time improvement is claimed without profiling. |
| T06 / P2 | Query `limit` must be 1–10,000 (`src/telemetry/query.rs:118–124`) | Avoids large Vec/output requests | Large local diagnostic investigations rejected despite a million-record capture budget | **REMOVE** the arbitrary hard maximum by paginating/streaming requested output. Current `export` is already an unbounded paged alternative; retain a useful small default. |
| T07 / keep | Application ID/schema checks and exclusive schema claiming (`sqlite.rs::initialize`; query.rs:62–81) | Opening an unrelated SQLite filename must not create/overwrite application tables there | One small local metadata check on open | **KEEP** ownership and compatibility check. Failure disables the observer (T02), not the transfer. It is not recovery authority. |
| T08 / keep | Read-only query connections and 128-record export pages ending DB transactions before writes (`query.rs:62,351–398`) | A slow output reader should not hold a DB read transaction and pin WAL indefinitely | Bounded page allocations; no refusal of whole export | **KEEP**. This reshapes normal flow rather than adding a blanket restriction. |
| T09 / keep | Error-chain maximum 32 with explicit truncation (`telemetry.rs:776–800`) | A real cyclic `Error::source` implementation otherwise loops forever while recording an error | Deep chains truncated visibly | **KEEP** bounded traversal. Identity-based cycle detection could retain longer acyclic chains, but no user benefit justifies extra machinery now. |
| T10 / keep | Paths, endpoint aliases, OS codes and error text recorded; no global argument/env/payload capture (`telemetry` vocabulary; ongoing SSH instrumentation) | Captures operational diagnosis without copying unrelated credentials or file payloads | Some command-specific details require intentional attributes | **KEEP** focused semantic instrumentation. Do not redact filenames/hosts or discard error chains merely because they may be private. The SSH phase gap is instrumentation omission, not a reason for global secrecy. |

The [OpenTelemetry error-handling guidance](https://opentelemetry.io/docs/specs/otel/error-handling/)
provides an industry comparison: observation failures should not unexpectedly
change the instrumented application's runtime behavior, and suppressed exporter
errors need self-diagnostics. Its allowance for initialization failure is not a
requirement to abort msync; the user's delivery-first contract calls for a more
useful fallback here. The [batch processor contract](https://opentelemetry.io/docs/specs/otel/trace/sdk/#batching-processor)
also distinguishes a bounded queue from making application callbacks block on
the exporter. These references inform the mechanism, not a demand to replace the
existing local recorder with an OpenTelemetry dependency.

## Editor, formatter, authentication UI and doctor inventory

| ID / priority | Mechanism and source | Concrete benefit or failure model | Normal-user cost | Disposition and simpler design |
| --- | --- | --- | --- | --- |
| E01 / P1 | Windows clipboard calls `invalid_controls(text,true)`, rejecting TAB anywhere (`src/editor.rs:468–474,505–516`) | The renderer cannot safely accept embedded ANSI/control sequences | Ordinary tab-separated or tab-containing pasted source is entirely rejected although typed TAB is allowed; hint tells user to change content | **REPLACE:** support TAB consistently in renderer/paste flow; retain narrow rejection or escaping for actual ESC/C0 commands. This is a renderer limitation, not evidence pasted TAB is hostile. No native Windows desktop probe was run in this audit. |
| E02 / P2 | All LSP methods using `opened` refuse documents above 8 MiB (`src/language_server.rs:194–207,318–325`; documents.rs:12) | Heavy parse/compile work can hurt editor responsiveness | Hover, completion, formatting etc. disappear at one arbitrary size; full source is already received, stored and indexed, so it is not a transport memory boundary | **REPLACE:** budget expensive semantic analysis independently; preserve cheap lexical/navigation services and allow the user to request explicit analysis. Measure generated-script behavior before selecting a budget. |
| E03 / keep | UTF-16 edit-range validation and stale version rejection (`documents.rs:128–162`) | LSP client offsets must not corrupt UTF-8 or apply old edits over new text | Malformed edits logged and document remains unchanged | **KEEP** protocol correctness. No attacker assumption required. |
| E04 / keep | Analysis coalesces jobs by URI and publishes only matching generation; compile-only execution (`analysis.rs`, `language_server.rs::publish_completed`) | Stale diagnostics must not overwrite fresh ones; editing must not execute programs/authenticate | Some old requests are discarded rather than completed | **KEEP** demand ownership and effect-free editor behavior. Do not add global workspace trust gates for user-authored source. |
| E05 / P2 | Formatter refuses selected symlink (`src/main.rs:1010–1016`) | Atomic rename of the link pathname would replace the link, not update its target | Proven writable linked-script formatting refusal | **REPLACE:** resolve the explicitly selected target and atomically update that actual file while preserving the symlink. A broad ban is not the only way to avoid destroying indirection. |
| E06 / P2 | Deterministic `.NAME.PID.fmt.tmp` stage fails on collision and asks user to inspect it (`main.rs:1024–1044`) | Must not overwrite/delete an object this invocation did not create | Stale file from PID reuse or a second format in the same process blocks otherwise normal work | **SIMPLIFY:** exclusive creation with fresh names/retry; cleanup only the successfully owned stage. Ownership check stays, guessed filename does not become a user obligation. |
| E07 / keep | Formatter refuses read-only source, uses owned staged atomic replacement (`main.rs:1018–1022,1046–1056`) | Rename in a writable parent could otherwise bypass deliberate read-only intent; partial output must not truncate original | Explicit readonly scripts need a writable copy/change of permissions | **KEEP** readonly intent and atomic write. Evaluate `sync_all` against the formatter's actual power-loss contract if it is measured as material; do not add a general publication journal to formatting. |
| E08 / keep | Render control/bidi characters as escapes in human diagnostics (`src/presentation.rs:278–288`); raw machine outputs left undecorated | A filename containing ESC or bidi should not rearrange/hide another diagnostic | Less literal visual rendering, but all characters remain identifiable | **KEEP** targeted display escaping, not whole-message suppression. This handles accidental filenames as well as hostile ones at low local cost. |
| E09 / keep | Askpass gets a native masked dialog; no app-owned password persistence; OS/agent remains auth authority (`src/windows_askpass.rs:353–401`, `src/auth.rs`) | User must identify which connection is asking; plaintext storage is not required for reuse | Repeated authentication is costly because Windows transport lacks reuse, not because password buffers are short-lived | **KEEP** credential lifetime; repair transport reuse instead of adding an app password cache. Keep user-selected external askpass override. |
| E10 / keep | 1024 UTF-16 CredUI message budget and 1023-byte askpass answer bound, newline/Unicode framing validation (`windows_askpass.rs:52–99,118–216,353–358`) | Actual native UI and OpenSSH line-protocol limits | Very long display fields elided; impossible answers rejected | **KEEP** genuine external constraints. Long connection/task fields are shortened for display rather than aborting an otherwise valid connection. [Microsoft CredUI API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creduipromptforcredentialsa) documents the message capacity. |
| E11 / keep | Volatile wipe of owned password buffers, avoiding reallocating UTF-8 answer buffers (`windows_askpass.rs:40–49,82–99`) | Reduces stale credential material retained after releasing application-owned buffers | Small linear pass over at most about 1 KiB; no new user refusal | **KEEP** this cheap, narrow mechanism, not a claim to defend against an administrator reading live process memory. [Microsoft's credential guidance](https://learn.microsoft.com/en-us/windows/win32/secbp/asking-the-user-for-credentials) supports identifying the requester and clearing owned buffers. |
| E12 / keep | REPL in-memory 1,000-entry history and ignorespace (`editor.rs:72–84`) | Bounded session UI state, no mandatory history-file ownership | No cross-session recall | **KEEP** current scoped release choice; the existing UX note explicitly says persistence was not necessary, not forbidden for security. Persistent history is a future user feature, not an attack-model argument for imposing amnesia. |
| E13 / P2 | Windows doctor excludes network-mapped/UNC executable candidates (`src/doctor.rs:114–133`) | Read-only inventory must not unexpectedly contact a network share | It can report “not located” although actual process resolution may find the user's client on that share | **REPLACE** absence wording with “network search path not inspected” and retain skipped path provenance. Keep doctor passive by default; offer explicit endpoint/client diagnosis rather than globally probing/authenticating. |
| E14 / keep | VS Code starts configured executable with `shell:false`, no shell-argument string (`editors/vscode/extension.cjs:9–25`; package.json:50–54) | Exact executable invocation, correct space-containing pathname behavior | Users needing a wrapper must name an executable wrapper | **KEEP** straightforward argv ownership; do not mistake avoiding an unnecessary shell for elaborate security. Add typed `serverArgs` only for a demonstrated workflow. |

## Distribution and verification inventory

| ID / priority | Mechanism and source | Concrete benefit or failure model | Normal-user cost | Disposition and simpler design |
| --- | --- | --- | --- | --- |
| P01 / keep | Installer owns only packaged files and its inserted user-PATH component (`packaging/windows/msync.iss`; windows-packaging.md) | Uninstall/upgrade must not delete user config/data or unrelated PATH entries | Ownership metadata and a small installer-local mutex | **KEEP**. This is essential user-data correctness, with actual upgrade/install acceptance, not an assumed hostile local user. |
| P02 / P2 | PATH reread mismatch raises “Retry the installer” (`msync.iss:263–268`) | Another application may edit HKCU PATH concurrently; overwriting would lose its change | Rare real concurrency becomes user retry instead of installer resolution | **SIMPLIFY:** bounded reread/rebase of the one owned component; preserve unrelated current bytes and stop only if the OS cannot apply the edit. Keep ownership and avoid pretending the mutex controls other installers. |
| P03 / keep | Static CRT build and optional PE import audit, version checks, package manifests/checksums (`packaging/windows/build.ps1:32–74`, Linux build and release workflow) | Packaged binary should run without the builder's CRT and match the named version | Build-time verification only; missing optional dumpbin already warns rather than stops | **KEEP** practical portability and identity checks; no runtime tax. |
| P04 / keep | Published release/tag bytes immutable; complete Windows/Linux/VSIX assets and SHA256SUMS required (`publish-release.cjs:15–17,35–64`; release.yml:212–234) | Users downloading a version must receive a stable artifact set; missing packages are a concrete delivery failure | A real defect needs a new version, not silent replacement | **KEEP** contractual release integrity. Publication does not wait for unrelated Rust CI, lint or formatting checks. |
| P05 / P2 | Existing **unpublished** draft revision/assets mismatch aborts; unknown extra assets abort (`publish-release.cjs:19–20,77–84`) | Avoid accidentally publishing mismatched build bytes or unreviewed extras | Retrying a rolling-toolchain build of the same source can require manual draft cleanup despite no public immutable artifact yet | **SIMPLIFY:** distinguish unpublished managed draft retries from published artifacts. Replace known managed draft assets after fresh package acceptance; do not silently publish or delete unknown user-owned extras. Keep published bytes immutable. |
| P06 / P3 | Annotated-tag indirection maximum 16 (`publish-release.cjs:27–29`) | Bounds repeated GitHub API reads for an abnormal tag chain | Technically valid deeply nested tags refused, no repository workload evidence | **REMOVE** arbitrary refusal when convenient; this is not a material user-latency issue and does not justify urgent generic recursion machinery. Normal release tags have one target. |
| P07 / P2 | CI selector defaults to full matrix for all unmatched `src/` or `tests/support/` changes and `.github` (`scripts/ci-select.ps1`) | Avoids silently omitting affected tests when ownership map is incomplete | Newly introduced unrelated module/support changes trigger broad repeated regression | **SIMPLIFY:** maintain contract/target ownership as modules evolve and log why full selection is needed. Keep explicitly requested full runs. This is test-selection policy, not a security boundary or new quality gate. |
| P08 / keep | Local fmt/strict analysis and selected tests; remote native/package acceptance; no remote lint gate (`scripts/check-local.sh`; .github workflows) | Linux-only compilation cannot establish Windows UI/installer behavior | Selected local work plus relevant remote acceptance | **KEEP** current split; do not expand this audit into mandatory all-target regression. |
| P09 / keep | License notices sourced from resolved packages and pinned supplements (`packaging/licenses.py`) | Distributed dependency license text must correspond to actual packages | Packaging-only hashing/text collection | **KEEP** distribution completeness; not a runtime security or performance mechanism. |

## Recommended execution order

1. Fix **C01/C02** with one effective-config boundary. The same architectural
   change repairs overrides and inactive options; do not add special-case catches
   for each reproduced input.
2. Fix **T01/T02**, preserving rich diagnostics and exact transfer status. The
   end-to-end test must prove target bytes and status under an exporter failure,
   not just assert a warning string. Separately replace queue backpressure and cap
   handling without claiming an incomplete trace is complete (**T03/T04**).
3. Repair Windows TAB paste and symlink formatting (**E01/E05**) with real native
   editor/CLI journeys; retain narrow genuine protocol/display constraints.
4. Remove policy-only caps (**C03/T06**) and introduce degradation rather than
   blanket LSP loss (**E02**). Recheck any advertised API behavior before changing
   public defaults.
5. Tackle typed recorder data flow and packaging retry details after direct
   user-facing failures. Do not rewrite already-correct installer ownership,
   release immutability, UTF-16 correctness or credential-buffer lifetime merely
   because those mechanisms are associated with safety.

## Implemented normal-user follow-up (2026-11-01)

The user's follow-up explicitly chooses retryable file operations over defensive
admission. Configuration no longer has regular-file/1 MiB intake policy or
pre-overlay semantic validation. Runtime values are validated after all CLI
overrides; `config` can display them without opening resources. SQLite settings
are checked only when SQLite opens, and exporter failures warn without changing
the operation outcome. Display interval zero disables progress; arbitrary
24-hour limits are removed. Default recorder quotas use actual database integer
representation rather than one-million-record/256-MiB limits, while explicit
quotas remain recorder-only budgets. Producers use nonblocking queue admission;
recording gaps are counted and persisted as an incomplete/error session.

TAB and literal control input are accepted; terminal rendering does not emit
control bytes as terminal commands. Formatting resolves an explicitly selected symlink target,
uses ordinary kernel replacement rights rather than a readonly precheck, and
retries fresh exclusive stage names instead of asking the user to inspect a
stale guessed name. Object intake reads normally without an object-only 64-MiB
ceiling. Telemetry query limits retain SQL representability, not a 10,000-row policy
maximum; zero naturally requests an empty result. Public parsing, version/type semantics and native IO
errors still describe the requested operation; they are not security gates.

The LSP follow-up removes the 8-MiB whole-document refusal from both request
lookup and diagnostics publication. Doctor's passive Windows lookup now records
uninspected search candidates and reports an incomplete search instead of
claiming an executable is absent. It still does not start clients or contact
UNC paths merely to inventory them; actual execution keeps normal native lookup.

Doctor no longer inventories SCP as a required/active transport after the native
single-channel SSH rewrite removed its runtime use. The remaining inventory is
for actual optional launchers, not obsolete protocol implementation details.
