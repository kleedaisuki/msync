# O2 investigation result publication

The three `o2-cost-90857bf3`, `o2-address-repair` and `o2-store-partitions`
directories retain the causal experiment, intermediate attempts and final
repair measurements, including failed or aborted attempts.

Published JSON replaces the checkout's absolute root with `<repo>` and the
user-home prefix with `<home>`; expand them when reproducing a command. Timing observations, work
counts, source contents/digests, binary digests and exception context are not
removed. File/receipt digests embedded in observations refer to the original raw
run files, not necessarily the path-normalized published copies. The
[o2-publication-manifest.json](o2-publication-manifest.json) maps each original
file digest to its published digest. Unmodified raw files are also retained
locally under `artifacts/diagnostics/o2-publication-raw`.

The [investigation](../o2-compile-cost.md) describes the phase-sampling,
intermediate controls and final 60-pair comparison. Product code does not
depend on the diagnostic scripts or result files.
