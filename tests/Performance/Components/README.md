# Native component profile

This probe calls the production parser, compiler, optimizer, artifact codec,
contextual lowering and interpreter APIs directly. It has no alternate evaluator
and does not execute filesystem observations or transfers. The two value-oriented
workloads finish by returning an actual Path; the expected value is checked
before measuring VM execution.

```sh
dotnet publish tests/Performance/Components/Msync.Components.csproj \
  -c Release -r linux-x64 \
  --artifacts-path .temp/performance-v080/current/components/build \
  -o .temp/performance-v080/current/components/native
.temp/performance-v080/current/components/native/msync-components \
  --sizes 1,64,256,1024 --samples 31 --warmups 3 \
  --output .temp/performance-v080/current/components/baseline.json
```

On Windows, publish for `win-x64` and invoke `msync-components.exe`; the APIs and
fixtures are otherwise identical. Use `--stages parse,artifact_decode,vm_no_io`
to select only the affected components after a change. Coordinate with other
benchmark owners before building or collecting samples.

Three shapes distinguish different costs:

| Shape | Constants and locals | Measured VM work |
| --- | --- | --- |
| `transfers` | Unique source/target constants, no local bindings | Not executed |
| `generic_values` | One typed function, N retained Path bindings | Call and join paths |
| `scope_values` | Same function selected through a lexical Scope | Member lookup, call, join |

`compile_o0` includes parsing, inference/lowering and basic-block construction;
`compile_o1` additionally includes the normal optimizer. There is no separate
public inference/lowering entrypoint, so those phases are not assigned invented
individual timings. `artifact_encode` includes the production validation and
in-memory serialization; `artifact_decode` includes validation and block
construction. `artifact_validate` and `form_blocks` isolate those mechanisms.
`bind_and_lower` includes contextual binding and execution lowering, while
`execution_lower` operates on an already-bound module. Cumulative stages are not
summed as though they were disjoint.

One calibration invocation selects 1–256 operations per timed sample, targeting
about 2 ms. Three untimed batches warm the static APIs; 31 sample values supply
nearest-rank p50/p95 and retain every observation. Allocation bytes/op are
cumulative managed allocation traffic measured in a separate batch, not RSS or
live heap size. Gen0/1/2 collection counts cover the timed interval. The final
result is retained to prevent eliminating returned values. Fixture generation,
artifact setup, source identity, expected-result checks and report serialization
occur outside timed intervals.

Reports include the native image SHA-256 and the actual compiler/Core/runtime
assembly informational versions, including their build commits. The checkout
commit at execution is recorded separately because an independently frozen image
can run after the shared checkout has advanced. A managed build may check probe
correctness, but its timings are not production-performance evidence.

`msync-components --check-interner` runs the focused constant-index contract
without timing: first-insertion order, duplicate identity, rollback of a failed
submission, source-free artifact execution, and retained runtime values.
