# Selective standard-library loading

This component probe measures the actual compiler's unoptimized parse/infer/lower
path, not an alternate namespace model. Run each case in a fresh process to
separate first embedded-source use from subsequent compilations:

```sh
dotnet publish tests/Performance/StdlibLoad/Msync.StdlibLoad.csproj \
  -c Release -r linux-x64 -o .temp/stdlib-load/native
.temp/stdlib-load/native/msync-stdlib-load bool 15
```

Cases are `none`, `bool`, `ranges_common`, `ranges`, `cast` and `functional`.
The first three use pre-existing source APIs and form fair old/new controls.
Reports include exact
image/build identity, all warm samples, cold/warm allocation, and raw function,
constant, instruction and slot counts. No clock/allocation threshold is a CI
gate. O0 is deliberate: it exposes unwanted library construction instead of
letting DCE hide it. Timings cannot be called Native AOT evidence when the probe
was run as a managed application.

The composed user journey is
`tests/Msync.EndToEnd/stdlib_experience_journey.py`; it separately checks actual
source-free execution, IO bytes and editor navigation into embedded sources.

## Source-export demand after the canonical family expansion

Additional current cases are `container_list`, `container_module`, `meta`,
`shell` and `shell_prelude`. `container_list` selects the List family directly;
`container_module` deliberately obtains a first-class module and retains its
complete public shape. `shell_prelude` derives the same ordinary alias bindings
as the REPL from embedded source. `legacy_list` is only a historical-image
control for the removed factory API, never a candidate API requirement.

For inexpensive managed component comparisons, `-p:FrozenAssemblies=DIR`
references immutable Core/Compiler DLLs instead of rebuilding their projects.
Set `-p:PublishAot=false` and give restore its own disposable
`-p:NuGetLockFilePath=ABSOLUTE_PATH`; do not replace the publication lock files
with a reference-only experiment's resolution. Reports record compiler image
SHA-256 and Debug/Release configuration to prevent mismatched comparisons.

The source-demand experiment under `.temp/stdlib-expansion/frozen-projection`
copies the same compiler and embedded library once, then changes only the call
to `PrepareStandardDemand` in its control. Shared frozen Core and identical
probe source keep other implementation changes out of the comparison. Nine
warm O0 observations give these managed Release component results:

| Source selection | Before → demand-selected p50 ms | Allocated bytes | Raw functions / instructions |
| --- | ---: | ---: | ---: |
| List construction and size | 109.395 → 6.326 | 14,148,333 → 938,385 | 857 / 9028 → 43 / 452 |
| Shell working directory | 25.810 → 0.505 | 2,738,287 → 74,218 | 226 / 2090 → 2 / 34 |

The comparison exposes compiler construction cost, not native startup or file IO
throughput. The source manifests and raw JSON observations retain the frozen
input identities. A later allocation-free qualified-path walk and a no-`$`
source fast path remove planner scratch without changing the selected graph.
Persistent sessions and editor analysis retain complete modules because future
submissions or interactive inspection may request additional fields.

[`stdlib_expansion.py`](../stdlib_expansion.py) measures actual CLI cold
processes and Linux PTY / Windows ConPTY first-prompt and first-Tab behavior.
Completion is checked by evaluating the selected List expression or transferring
the selected file, rather than equating an arbitrary screen repaint with a
correct insertion. Those results must be labeled separately from this managed
component table.
