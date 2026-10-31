#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
before=${1:?usage: bench-block-frontier.sh BEFORE_BLOCKS_RS [SIZES] [SAMPLES]}
sizes=${2:-100,1000,10000}
samples=${3:-7}
mkdir -p "$root/.temp"
work=$(mktemp -d "$root/.temp/block-frontier-bench-XXXXXX")
# include! cannot adopt the included source's inner module documentation.
tail -n +2 "$before" > "$work/before.rs"
tail -n +2 "$root/src/execution/blocks.rs" > "$work/after.rs"
MSYNC_FRONTIER_BEFORE="$work/before.rs" MSYNC_FRONTIER_AFTER="$work/after.rs" \
    rustc --edition 2024 -C opt-level=3 "$root/scripts/bench-block-frontier.rs" -o "$work/benchmark"
"$work/benchmark" "$sizes" "$samples"
