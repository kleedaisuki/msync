# Real scheduling components

This probe prepares executable typed bytecode before measuring. Every operation
uses the normal transfer engine, owned staging, metadata and fsync behavior;
every destination is checked against a fresh random 64-byte generation after
timing. It does not replace transport with a completed-task mock.

```sh
dotnet run --project tests/Performance/SchedulingComponents -- \
  --root .temp/scheduler-components --cases independent,observe_directory \
  --sizes 1,100,1000 --samples 1 --warmups 0
```

Cases:

- `independent`: one shared source, independent sibling outputs.
- `chain`: each destination is the next operation's source.
- `observe_directory`: copy, explicit acquire, then a native `fs.exists` of the
  output directory. This discriminates historical prefix-index traversal from
  genuinely overlapping active work.
- `gated_literal`: a real released child waits for a host-created gate before
  printing the payload. All String-to-file operations are admitted before that
  child can complete, separating admission from later transport/draining.
- `gated_shared`: the child first publishes the shared source; queued readers
  depend on that writer while admission proceeds. The original source contains
  stale bytes, so removing a prerequisite cannot pass output verification.

The gate child is the same native component executable, not Python or a shell.
It uses a filesystem notification plus an initial existence check. The host only
opens the gate after observing all admissions. Admission includes actual process
launch and path/resource bookkeeping; it is not represented as isolated CPU
instruction time. For other cases admission is reported only if all transfers
were admitted before the interpreter's initial suspension.

Constructor and execution timings/allocations are distinct. The bytecode module,
fixtures, source generation and output verification are outside those intervals.
The two-minute cancellation source is a hung-process watchdog, not a performance
assertion. Raw execution, allocations and all public scheduling statistics are
retained per observation. No thresholds are CI quality gates.

For accepted performance comparisons publish both source snapshots with Native
AOT and run the alternating harness:

```sh
python3 tests/Performance/scheduler_components.py \
  --before /absolute/path/to/before/msync-scheduling-components \
  --after /absolute/path/to/after/msync-scheduling-components \
  --sizes 1,100,1000 --rounds 3 --samples 3 --warmups 1 \
  --output .temp/scheduler-components/paired.json
```

The companion `tests/Performance/scheduler.py --stage executable` measures the
whole native CLI while preparing each image's own `.mexe` outside timing.
Its default `--stage source` retains the historical source-inclusive journey.
