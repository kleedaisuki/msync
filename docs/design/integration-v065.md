# v0.6.5 integration workboard

## Objective and starting state

The complete objective is inferred semantic types and operator capabilities,
canonical `$view::` built-in APIs, measured non-payload SSH performance, general
scope qualification of lexical function results, scheduled conditionals, ordered
user-created ranges, and the outstanding historical repairs. The package/editor
delivery version is 0.6.5; versioning is not evidence that the implementation is
complete. The subsequent user instruction authorizes merging to main and
publishing v0.6.5 with platform packages, the VSIX and checksums. Previously
published tags and assets must remain unchanged.

The starting source is `d501fd26614055af7a0f77b58a7b20bbb6cadd99`, on topic branch
`codex/v0.6.5`. It includes the automatic empty-unit recovery repair and
local-first validation policy. User-owned untracked `docs/analysis/` is unrelated
to this implementation and is not included in delivery changes.

## Current accepted delivery

The final implementation source is `8818d117172d67ddb9b4145ea645ccc759b20c0f`;
`01aef79bb65e316dd2558f985985f91b8dd8eb30` adds only the portable real-TCP-reset
failure fixture. Production sources/manifests/embedded README are unchanged
between those revisions. The immutable `msync-v065-delivery` binary SHA-256 is
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`,
matching library `0ef4f615b02f4f8b611d4cd3d54d60d9d06cb161b9f10192aa39b543bc7c3aeb`.
The exact committed build-source archive, identity JSON and code-file hashes
are adjacent under `target/performance-v065/baselines/`.

- The delivery image passed all eleven public acceptance functions and nine real
  VS Code host phases; the source-aligned VSIX remains 122,091 bytes / ten files,
  SHA-256 `187f1bf25477f25b57e5b25b84499f00845cd17cb90f887892bc290efe1ff84c`.
- [Native Rust run 41240455544](https://github.com/kleedaisuki/msync/actions/runs/41240455544)
  passed on Windows and Ubuntu, including native terminal queue/wake and Windows
  credential checks. The Windows path-model error is corrected centrally, not
  hidden behind condition-specific global waits or platform skips.
- [Package run 41239526348](https://github.com/kleedaisuki/msync/actions/runs/41239526348)
  passed Windows install/repair/upgrade/uninstall, Linux portable archive and real
  editor-host acceptance on the same production source. Publication was skipped.
  Downloaded complete assets and checksums are under `dist/packages/v0.6.5-8818d11/`.
- Current-image SSH response medians improve by 1.67–3.47x on the four transfer
  cases, all five of five paired wins. The new 60-row dataset retains setup CPU
  costs (+32.6% download, +67.6% error, +6.6% exists) and mixed single-exists
  latency; bulk CPU falls 40.4%. Remote sessions are not removed. Four separate
  exact fractional/pre-epoch metadata transfers passed. See the delivery appendix
  in [the SSH record](ssh-performance-v065.md).
- Current compiler data, not the old `e18` result, governs delivery claims.
  Layouts and allocation traffic stay unchanged; amortized paired medians add
  about 0.303ms per 1,000 symbols and 0.559ms per 1,000 conditions. This bounded
  feature/correctness cost is accepted, with no operator-specific CPU attribution
  or universal speedup claim. All earlier rows remain separately identified in
  [the compiler record](compiler-performance-v065.md).

The older checkpoints below retain their actual source/image scope. They do not
replace this delivery evidence or retroactively acquire its identity. Following
explicit user authorization, main is fast-forwarded to this delivery and the
release workflow publishes v0.6.5 after package/editor installation acceptance.
Marketplace publication is not part of this release.

## Published release

Following explicit user authorization, the accepted branch was fast-forwarded
into main and publication notes committed as
`c189b0106d30261512b942c1bbdc5836987453a2`. Production sources, manifests and
embedded README still match the accepted `8818d11` implementation.
[Release workflow 41271582786](https://github.com/kleedaisuki/msync/actions/runs/41271582786)
passed Linux portable acceptance, Windows installation acceptance and the actual
VS Code host, then published the complete
[v0.6.5 release](https://github.com/kleedaisuki/msync/releases/tag/v0.6.5)
at 2026-11-01 04:08:41 UTC. The immutable tag points to `c189b01`; v0.6.0 remains
at `0a7421c8959a3ad16dbb9e3be5176c1167e251c4`.

All four public assets were downloaded into `dist/packages/v0.6.5-published/`
and checked successfully against the release's `SHA256SUMS`:

| Published asset | SHA-256 |
| --- | --- |
| `msync-0.6.5-linux-x86_64.tar.gz` | `27e6c0f84035e182201cd78fbbd118ac3de824bfbf91b5245ee0203b59dc6a57` |
| `msync-0.6.5-windows-x86_64-setup.exe` | `a7ff027b07befd216d4fbb579d14cddfecd6297f2ba068b624a9c995ef0d1816` |
| `msync-0.6.5-windows-x86_64.zip` | `08b0e3e9d8d9b59b3a624d251374078a0b397ca0c7b0aaf9d4ff0d0e59c7c865` |
| `msync-language-0.6.5.vsix` | `3dc9e293fb5d4a3678101e65410d00ae6b5f9edfb1b553436126c61a7cb15a9f` |

These are the public archive identities, not the earlier local/development
archive identities above. No published bytes were replaced and no tag was moved.

## Contracts and authoritative completion evidence

| Requirement | Required evidence | Ownership / durable record | State |
| --- | --- | --- | --- |
| Explicit internal Path/String/Scope/Bool/Range and operator traits | Central operand/result capability table exercised by inference, negative type tests and typed objects; no source annotations required | Language lead; [type contract](type-system-v065.md) | Locally verified on final image |
| `/` and nested `::` | All requested operand combinations, nested scopes and call results; type-directed errors instead of spelling exceptions | Language lead | Locally verified; legacy diagnostics preserved |
| `$view::` built-in API namespace | Compilation, runtime, canonical help/completion/grammar and actual editor journey; previous working aliases remain compatible | Language + toolchain leads | Final native editor host and source-free journeys passed |
| General `$wsl::codex(config.toml)` composition | Supplied example and renamed/nested lexical functions; deferred path/home resolution under the selected scope, source-free object/IR behavior | Language lead + independent acceptance | Final-image source/object/IR evidence passed; no native WSL claim |
| `if release`, `if acquire`, `if acqrel` | Existing scheduling flags, strict bool condition and actual held-work admission/order, not parser-only acceptance | Language lead + acceptance | Directional flags and held-work/selected-auth journeys passed |
| Ordered `[ ... ]` ranges | Bool/String/scoped Path elements, mixed permitted scalar joins, lexical function expansion, stable order, lazy consumers, invalid operations rejected statically | Language/range owner + acceptance | Ordered Bool/mixed/callback/lazy and rejection journeys passed |
| Compiler performance | Matched representative compile/specialization workload, exact baseline/candidate identities, allocation/scaling observations where discriminating | [Compiler record](compiler-performance-v065.md) | Exact-image paired measurements accepted, with mixed outcomes retained |
| SSH performance excluding payload-network explanations | Real authenticated SSH workflow, phase/process/connection evidence, matched before/after small operations and unchanged recovery/cancellation/output | [SSH record](ssh-performance-v065.md) | 60 matched real SSH observations and separate exact-nanosecond checks passed |
| Historical public API and finite-selection atomicity | Existing execution tests plus real collision/empty-selection and source-free/contextualized control journeys; no weakened assertions | Compatibility worker + language lead; [historical inventory](history-v065.md) | Source/API fixes verified; old For bytecode migration explicitly documented |
| Delivery coherence | Cargo/lock/editor version agreement, local targeted lint/tests, usable VSIX and native-host integration at the integrated source | Root + toolchain lead | Local binary/rlib/VSIX and real host verified; native platform workflow remains supplemental |

Detailed independent journey design belongs in
[validation-v065.md](validation-v065.md). Tests and measurements are credited only
after inspecting their actual coverage and result, not merely because a helper or
manifest exists. Focused local work is primary; Actions supplies platform-specific
behavior and packaging evidence, never a separate lint/status publication gate.

## Integration decisions

- The language lead owns AST/compiler/semantic-HIR boundaries and may delegate
  bounded parser and range implementation. The type/operator table is the shared
  interface; consumers must not invent independent inference rules.
- `$view::` is canonical, with compatible established aliases. A range may join
  the finite permitted scalar types; its consumers must be valid for all possible
  members. A path cannot silently become string data or a bool condition.
- Scope qualification applies to normally inferred lexical results. Endpoint and
  home binding remain deferred until the scope is known. This must work for a
  renamed function and not depend on the spelling `codex`.
- The execution session owns recovery roots and retained owner locks. New syntax
  and no-work optimizations must preserve these lifetimes. Finite implicit range
  transfer remains one recoverable file statement; an explicitly written `for`
  can still introduce per-iteration statement boundaries.
- Temporary fixtures/logs stay under `.temp/v065`; frozen source/binaries and
  managed test dependencies belong under `target/` or established package-manager
  locations. Cargo's own lock serializes shared-target builds; measured performance
  windows must be exclusive and announced to other owners.

## Initial observations

The unchanged baseline executable rejects the qualified-symbol example, a Bool
list, `$view::single` and `if release true`; these are useful discriminators, not
implementation progress by themselves. Historical inspection also found that the
public executable compile adapter omitted nested control contextualization, and
that an implicit finite selector transfer could publish earlier members before a
later collision. Both require production repairs rather than acceptance changes.

The current user is not a member of host group `docker` (gid 990), the daemon
socket is `root:docker` mode 0660, and noninteractive sudo is unavailable. This is
not a task blocker: real user-space packaged OpenSSH is the selected local SSH
fixture alternative; host permissions and authentication are not changed.

Local cross-platform-selector verification is executable rather than deferred to
Actions. The official PowerShell 7.6.9 Linux x64 archive is retained under
`target/test-deps/powershell/v7.6.9/`, SHA-256
`1da6d6d6035d651f20e70ddee7c4c15ac6ab8b9fb40ee92240d91263f28a884b`,
from the matching [upstream release](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.9).
With isolated HOME/cache/state beneath `.temp/v065/pwsh`, its `pwsh -NoLogo
-NoProfile -File scripts/test-ci-select.ps1` passed the existing selector,
version-only/Git-fixture assertions and the new type/range/toolchain target
registrations. This setup does not install tools globally or start Rust tests.

## Broad local compatibility checkpoint

After the new type/control/publication interfaces and transport changes became
coherent, local `cargo fmt --all -- --check` and
`cargo clippy --locked --all-targets -- -D warnings` passed. A single justified
`cargo test --locked --tests --no-fail-fast` then exercised the remaining old
contracts as well as new journeys; the prior platform run had stopped at a now
repaired selector failure and therefore could not establish later-target health.
Benchmarks were excluded, and no repeat full matrix is planned.

That checkpoint recorded 1,193 passing functions, 14 failures across ten targets
and 38 ignored entries (119 target-result records). Six platform-gated zero-test
targets are not counted as platform acceptance. The failures are actionable
integration input, not accepted regressions: source-owned type/provenance and
computed-member compatibility go to the language owner; obsolete nested-macro
and For-shape structural expectations go to independent acceptance for
contract-preserving updates; real PTY/background-parent lifecycle failures go
to the lifecycle owner. Only affected cases/targets are rerun after each repair.

The original complete log and compact summary are under
`.temp/v065/local-compatibility*`; focused repair records and the final
requirement audit below must supersede these failures with actual evidence.
These counts do not establish that the integrated delivery is already complete.

## Initial frozen local checkpoint (e18; superseded by delivery refresh)

The integrated release-mode binary is
`target/performance-v065/baselines/msync-v065-final`, SHA-256
`e18d9f36b905630b9915cc5634e4ff4833a66ee34924650fc555913482433f7f`;
the matching rlib is `libmsync-v065-final.rlib`, SHA-256
`c85145101acff3c1ff1f7534fe1f1b775ca6658bed701c9ed5604f8bcb5b5c05`.
Its source-build archive, file manifest and identity JSON are adjacent. All 146
production-source, manifest and embedded-README files were checked unchanged
after final functional and performance execution. Later edits affect test-driver
executable selection and durable result notes, not the frozen implementation.

The final image reports 0.6.5. Eleven independent acceptance functions passed in
one final run, with external commands routed to that exact image through the
bounded existing process helper. The earlier 14 compatibility failures were
closed by focused, not repeated full-matrix, checks: seven source-owned language
compatibility cases, five contract-preserving structural oracle updates, and
two lifecycle cases. The latter include the real Rustyline producer/reader
deadlock and the test's missing destination-admission handshake. The maintained
upstream-source correction and exact-byte provenance are in
`vendor/rustyline/README.msync.md`; the native queue/wake transports are not a new
terminal implementation or notification thread.

After these repairs, formatting and strict all-target Clippy passed locally.
The doctest command completed with zero doctests; it is not credited as additional
behavioral coverage. Seven license-collector cases and actual 145-package plus
Rust attribution generation passed, including correct vendored-dependency source
identification. Existing PowerShell selector checks, new typed/range/toolchain
registrations and native terminal transport selections ran locally using the
managed toolchain described above. Actions still has no fmt/Clippy/status gate;
the added dependency step tests native Unix/Windows wake behavior only.

The real managed VS Code 1.144/Xvfb host passed all nine phases against the frozen
server: activation, Bool diagnostic, edit repair, formatting, canonical API and
alias completion, qualified-function definition navigation, UTF-16 semantic
tokens and absence of DSL effects. The source-aligned VSIX is
`dist/editor/msync-language-0.6.5.vsix`, 122,091 bytes / 10 files, SHA-256
`187f1bf25477f25b57e5b25b84499f00845cd17cb90f887892bc290efe1ff84c`.
The complete evidence is in [the editor record](editor-tooling-v6.md).

Accepted performance scope is deliberately workload-specific:

- Six real loopback SSH cases have five matched AB/BA pairs each, with complete
  bytes, mode, whole-second mtime and located-error checks. Upload/download,
  five-file selection and five uploads improve response medians by 1.66–3.31x;
  repeated authentication falls from 8/3/16/40 to one. Remote session counts stay
  unchanged and client process counts rise by five on reuse cases. Bulk joined
  local-child CPU falls 39.4%; single-download and missing-error local CPU rise
  20.6% and 70.8%. This bounded setup-CPU/response tradeoff is accepted for the
  dominant multi-helper transfer workload, not described as universal CPU or
  process reduction. Server/WAN/Windows performance is not measured. Four
  separate final-image positive/pre-epoch upload/download checks preserve exact
  fractional timestamps.
- Compiler allocation counts, allocated bytes and all four measured public
  layouts are exactly unchanged on the old-valid workloads. At 1,000 conditions,
  lowering improves 9.730 to 8.083ms with seven of seven paired wins; capture,
  symbol and real-CLI results remain mixed. A 50-repeat discriminator does not
  reproduce the initial roughly 0.1ms small-symbol penalty and retains a mixed
  roughly 0.025ms paired residual. No production rewrite or universal speedup is
  justified by that noise. The new typed workload is reported only as absolute
  cost; the old 1,000-statement CLI timeout is not a speedup denominator.

An independent [validation audit](validation-v065.md) recomputed the paired rows,
checked exact image/probe/library identities and retained the negative findings.
No published tag or release asset has been replaced. Native platform/package
workflow results, when run on the development branch, are recorded separately
from the local evidence and do not become an external quality-status gate.

### Native test portability follow-up

The first native package workflow on development commit `149195a` passed Linux,
Windows install/repair/upgrade/uninstall, and the actual editor host; its publish
job was skipped. The independent Windows Rust job then exposed a fixture that
asked Windows `SystemTime` to retain 1ns rather than its 100ns representation.
The portable case now uses 123456700ns and Unix retains the exact 123456789ns
assertion; production timestamp code did not change. The correction is locally
tested and linked to the standard-library contract in the metadata record.

Rebuilding after this cfg(test)-only correction changes the complete binary
hash to `b2acbce3454af4b4562f505d32d36d4a93e79d2ba31b9249ba2094b9fd8bf789`
and library hash to
`2edf7d0af9116e8a3ebff87c7b8cee39438a0f50a15fabc37804b28222868f4c`.
The executable's loadable code/data image and addresses are byte-identical to
the measured `e18d9f36` image: `objcopy -O binary` outputs compare equal, SHA-256
`2410bb173ce33a1bd08ef1210de50b7125a95d0e5639e9f2b24e7cb222bd5282`.
The first full-file difference is in the nonloaded string table. Original timing
rows retain their original image identities; they are not relabeled.

The first Linux Rust run reached custom benchmark executables under
`cargo test --all-targets` and was canceled by the newer source revision while
the 1,000-item object benchmark was still running. Cross-platform compatibility
now uses `--tests --no-fail-fast`, keeping performance probes local/explicit and
collecting later native failures without restarting successful targets.

### Windows resource-model and portable failure-injection closure

Native Windows then exposed two local-control waits and a staged-file fixture
problem. A host-independent probe proved that the shared comparison model
misclassified canonical extended drives as uncertain UNC roots, and also missed
normal/extended UNC aliases. Known prefix spellings now feed the same ordinary
drive/UNC model; IO operands are unchanged, and unknown devices/verbatim special
components remain conservative. Four direct/property cases (64 generated alias
and sibling cases) and the expanded 512-case candidate oracle passed locally.
The two original no-global-acquire journeys then passed natively.

Windows also correctly refused renaming the live destination owner after stage
admission. The background-failure journey now injects a real abortive TCP reset
through existing socket2 on every platform, preserving its unrelated-child,
error-before-EOF and exact-byte assertions and adding partial-output refusal.
No production share policy, timeout or platform coverage was weakened. The final
Windows/Ubuntu run linked above verifies that last portable fixture too.

## Relevant primary prior art

- [Rust operator traits](https://doc.rust-lang.org/reference/expressions/operator-expr.html)
  inform explicit operation capabilities rather than syntax-spelling dispatch.
- [Dunfield and Krishnaswami, Bidirectional Typing](https://research.cs.queensu.ca/home/jana/papers/bidir-survey/)
  motivates combining synthesis of values with checking expected operator and
  callback domains. This DSL does not import a higher-rank solver or require user
  annotations merely to follow that separation.
- [OpenSSH client configuration](https://man.openbsd.org/ssh_config) documents
  mature invocation/session connection reuse and private, endpoint-distinguishing
  control sockets. Adoption depends on measured orchestration costs and preserved
  credential/cancellation ownership, not an assumption that payload IO is slow.
