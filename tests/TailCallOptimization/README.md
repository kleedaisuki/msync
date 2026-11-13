# Native tail-call behavior

Run `dotnet run --project tests/TailCallOptimization -c Release`.

Run `python3 tests/TailCallOptimization/cli_journey.py PATH_TO_NATIVE_AOT_CLI`
for actual CLI source/portable/bound execution in Off/Linked/Force modes. Each
route checks the printed 100,000-step result. Source is removed before portable
execution/binding, and the portable input is removed before bound execution.
Commands, output, image/artifact hashes and failures remain in a unique child
of `.temp/native-tail/`; these are functional journeys, not timed comparisons.

Ordinary source is compiled at O0/O1/O2/O3, validated, executed, then executed
again after a strict native artifact roundtrip. The oracles cover recursive sums,
parallel argument exchange, shared captured writes and fresh escaped locations.
O2/O3 additionally execute 100,000 recursive steps without consuming recursive
interpreter frames. The result must be 5,000,050,000.

Off/Linked/Force native-IR controls additionally check externally bound result
writes, escaped result pointers, original call-site errors and exact logical
instruction/edge/target counters after tail handoff. The runtime uses fresh
activation storage; escaped locations are not recycled as the next frame.

`--small` isolates the short source/effect controls for diagnosis; it does not
replace the default deep-recursion acceptance. `--dump` writes source-free native
images and human-readable IR for the deep case below `.temp/native-tail-dump/`
without executing it.

The retired stack-bytecode pass identity, slot numbering and frame-copy error
whiteboxes are not native contracts. The runtime may optimize eligible calls,
but actual closure environments, captured location identity and effect lifetimes
remain observable. This executable is a behavioral gate, not a tail-pass counter.
