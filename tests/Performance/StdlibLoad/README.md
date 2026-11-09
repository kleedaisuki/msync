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
