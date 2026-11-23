# HPIR performance baseline: msync 0.9.3

2026-11-22 · Linux x64. This is the image and measurement manifest for the
[HPIR roadmap](../design/hpir-roadmap.md). Compare a release candidate to
the **promoted v0.9.3 image**, and use a separate same-toolchain rebuild
to attribute differences to implementation rather than to the build host.

## Frozen images

| Role | Source and package | Executable SHA-256 |
| --- | --- | --- |
| Published performance baseline | [v0.9.3 promoted assets](https://github.com/kleedaisuki/msync/releases/tag/v0.9.3), commit `d0f805cb205f021b4b8f46a9808db996193dcb46`, candidate workflow run `44715391210`. Linux archive SHA-256 `5840683227228a842751ec5d63396ffcde17aa37fc56dc97068250a9581652e9`. | `9edc10c0071b92dc16659f6546f367c21caaabcda9a241bdae36637c4c7c564b` |
| Same-host HPIR control | Exact v0.9.3 commit, SDK 10.0.403 / Native AOT 10.0.14. Linux archive in `dist/candidates/v0.9.3-hpir-control/`, SHA-256 `a105cd6548ee442adfdb82c6b187850c6ec6d3216404be5a422811c80d337089`. | `712a7157cb946ad1703e4ada34f931ad1000c54237f924aeb2b5b621c555ce4f` |
| Historical diagnosis | Same release revision; local archive in `dist/candidates/v0.9.3-local/`, SHA-256 `a08618f95e895a8353634023b788c9a1fc23ce2133d8d80ecb5018a277af0a35`. | `a0f93c7ad725cd7844a00ae45d7b57a6341139d8e40a0b1e610084ef4d69a952` |
| Historical counterfactual control | Local control image in `.temp/diag-093-counterfactual-out/`, built with SDK 10.0.404. Use only with the corresponding [attribution](0.9.3-runtime-attribution.md) ablations. | `d93039d238cea54daf0ba7c760ee83686bc377b37f3d6226878165e16b967c93` |

The immutable archive is at `artifacts/releases/v0.9.3/published/` locally.
Verify its published `SHA256SUMS`, extract it unchanged, and verify the
extracted `manifest.sha256` from the package root. All 468 entries verified
on the local image. The packaged executable passed
[packaging smoke](../../packaging/smoke.py): source, dry-run, source-free
portable/prepared artifacts, bound byte output and native SQLite telemetry.
The image and its adjacent SQLite library travel together; the latter has SHA-256
`b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07`.
The same promoted release records a Windows portable archive with SHA-256
`f0819c0bf4fb75b4ce57f942d2564af75a135da4eb9da6a8c719b0b6b3109c80` and
executable SHA-256
`4dc24dd91c2ca67a1d1fe156a93982c90b856437c845ed5fe224b5ceba403313`.
Its timing needs a Windows runner, not this Linux control.

The raw historical corpus receipts were recovered from
`.temp/benchmarks/diag-093-{a,b}/applications/result.json` and copied to
`artifacts/benchmarks/hpir-baseline/`. Both receipts freeze the
`a0f93c7a…` local build, **not** the promoted `9edc10c0…` build. They cover
reference distribution, standard scale, seed 17, CPU 6, JIT auto, and
telemetry off. Receipt A used source and bound, three samples with one
warmup; B used bound only, one sample without warmup. Trace/instruction
attribution, counterfactuals and single-version timings remain useful for
explaining the old path, not for claiming speedups against promoted bytes.
The pending-identity defect in that path is not a performance or semantic
oracle to reproduce in HPIR.

## Rebuild a same-host control

Use the tag as a separate checkout so the build has the correct revision
metadata and the packaging script sees the tag's tracked file inventory.
Do not overlay the SDK version on either source tree. Use a matching
native toolchain, runtime packs, Release configuration and RID to build
the v0.9.5 image. A local rebuild does not claim byte equality with the
Ubuntu 22.04 release runner.

```sh
root=$PWD
sdk="$root/.temp/hpir/sdk"  # .NET SDK 10.0.403, as pinned by the tag
control="$root/dist/candidates/v0.9.3-hpir-control"
source="$root/.temp/hpir-baseline/release-source"
git clone --shared --no-checkout "$root" "$source"
git -C "$source" checkout --detach v0.9.3
test "$(git -C "$source" rev-parse HEAD)" = d0f805cb205f021b4b8f46a9808db996193dcb46
test -z "$(git -C "$source" status --porcelain)"
export PATH="$sdk:$PATH" DOTNET_ROOT="$sdk" DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export SOURCE_DATE_EPOCH="$(git -C "$source" show -s --format=%ct HEAD)"
(cd "$source" && python3 packaging/build.py --rid linux-x64 --smoke --output "$control")
```

Record the archive and both executable payload hashes, `dotnet --info`,
native compiler and linker versions, OS/kernel, and the package smoke
result. The recorded control built on Debian 13.8 x64 (glibc 2.41, Linux
6.12.113, Clang 22.1.8, binutils 2.44). It passed the package smoke,
archive checksum and all 468 manifest entries; relative to the promoted
package only the executable and the SDK's third-party notices differ
in the payload manifest. The CLI assembly's informational version includes
the exact release commit. See `artifacts/benchmarks/hpir-baseline/` for
the checksummed build, toolchain, identity, and smoke receipts.

Use separate report lanes against the promoted image and against the
same-host control. The control is not a substitute for the promoted
release when reporting the user's speedup. No new timings were collected
alongside active implementation builds.

## Comparison protocol

Use the maintained [application corpus](../../tests/Performance/Corpus/README.md)
and [distribution suite](../../BENCHMARK.md); record the runner revision.
The corpus freezes the executable and adjacent dependencies of each
variant, generates one input/oracle per seed and distribution, and
prepares each variant's portable and bound artifacts separately. It uses
the same fixture working directory for both variants, with `--no-config`;
run detached artifact correctness separately from an unrelated directory.

```sh
# Use a quiet host, fresh work directory, and a permitted CPU for compute.
release="$PWD/artifacts/releases/v0.9.3/published"
unpack="$PWD/.temp/hpir-published-0.9.3"
(cd "$release" && sha256sum -c SHA256SUMS)
mkdir "$unpack"
tar -xf "$release/msync-0.9.3-linux-x86_64.tar.gz" -C "$unpack"
package="$unpack/msync-0.9.3-linux-x86_64"
(cd "$package" && sha256sum -c manifest.sha256)
published="$package/bin/msync"

# Set "$candidate" to the installed v0.9.5 Native AOT binary and "$cpu" to
# the selected CPU. Repeat in a separate work directory for the local control.
MSYNC_JIT=auto python3 tests/Performance/Corpus/run.py \
  --baseline "$published" --candidate "$candidate" \
  --route all --scale standard --distribution both --seed 17 --seed 503 \
  --pairs 7 --warmups 1 --profile profile --cpu "$cpu" \
  --work .temp/hpir-release-comparison
```

- **Core application cases:** `signal-hysteresis`/long bursts isolates old
  binding-induced culling; `numeric-record-stream`/sparse selection and
  `bounded-sensor-pipeline`/high rejection stress materialized and lazy
  Range respectively; `live-policy-dispatch`/dominant policy tests live
  closures; `set-dedup`/broad expiration tests layout/heap behavior;
  `unicode-path-manifest`/Unicode-heavy exercises host and guest call ABI.
  Use the actual observed shape counts from the report, not a production
  frequency assumption.
- **Mechanism diagnostics:** add `polygon-queries` for the second binding
  inversion and `bplus-inventory` to catch a performance loss hidden by
  removing inlining. Keep same-code/two-environment and different-code
  dispatch as separate cases. Root, frame-growth, return/unwind, pending
  identity and alias-lifetime journeys must pass independently of timing.
- **Guardrails:** run the distribution suite's `cli`, `io` and
  `interactive` groups with `--binary "$candidate" --baseline "$published"`.
  Keep fresh CLI startup, first deployment and redeployment, and persistent
  requests separate. Do not pin multi-core I/O to one CPU. Include both
  small-file-heavy and artifact-heavy I/O; check real bytes and metadata
  before interpreting a speed comparison.
- **Measurement gates:** primary measurements use the default runtime policy
  and telemetry off, paired AB/BA; separate opt-in profile runs establish
  causes. JIT-off controls answer whether the new execution model can run
  without legacy engine rescue; old JIT-off timings are not the release
  performance denominator. Track compile and bind outside execution:
  corpus preparation emits one observation each per fixture, so collect
  repeated paired preparation runs before characterizing bind latency.
  Report a credible gain against promoted bytes across the targeted work
  and demonstrate the removed cost (e.g. wrappers, bridge invocations or
  discarded facts) independently. Keep raw paired samples and check for
  effects of noisy neighboring work. No aggregate can hide a regression
  in a named guardrail.

Retain `result.json` and separate causal profiles in
`artifacts/benchmarks/`, and preserve input hashes and exact oracles.
One earlier [Linux distribution report](0.9.2-linux-x64.md) is useful for
fixture and guardrail selection, not as an HPIR comparison baseline.
