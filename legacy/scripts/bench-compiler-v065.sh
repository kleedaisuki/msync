#!/usr/bin/env bash
set -euo pipefail

if (($# < 2 || $# > 3)); then
  printf '%s\n' 'usage: bench-compiler-v065.sh BEFORE_PROBE AFTER_PROBE [OUTPUT_LOG]' >&2
  exit 2
fi
before=$(realpath -- "$1")
after=$(realpath -- "$2")
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
output=${3:-.temp/v065/compiler/paired.log}
pairs=${MSYNC_COMPILER_PAIRS:-7}
[[ "$pairs" =~ ^[1-9][0-9]*$ ]] || { echo 'MSYNC_COMPILER_PAIRS must be positive' >&2; exit 2; }
[[ -x "$before" && -x "$after" ]] || { echo 'Both probes must be executable' >&2; exit 2; }
[[ ! -e "$output" && ! -e "$output.rows" ]] || { echo 'Refusing to overwrite an existing measurement' >&2; exit 2; }
mkdir -p "$(dirname -- "$output")" "$output.rows"
sha256sum "$before" "$after" > "$output.images.before.sha256"
printf 'pairs=%s sizes=%s cases=%s samples_per_arm=1 warmup=per-case\n' \
  "$pairs" "${MSYNC_COMPILER_SIZES:-100,1000}" \
  "${MSYNC_COMPILER_CASES:-symbols,conditions,captures}" > "$output"
for ((pair=0; pair<pairs; pair++)); do
  arms=(before after)
  if ((pair % 2)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    probe=$before
    if [[ "$arm" == after ]]; then probe=$after; fi
    row="$output.rows/$pair-$arm.log"
    MSYNC_COMPILER_SIZES=${MSYNC_COMPILER_SIZES:-100,1000} \
      MSYNC_COMPILER_CASES=${MSYNC_COMPILER_CASES:-symbols,conditions,captures} \
      MSYNC_COMPILER_SAMPLES=1 "$probe" > "$row"
    sed "s/^/pair=$pair arm=$arm /" "$row" >> "$output"
  done
done
sha256sum "$before" "$after" > "$output.images.after.sha256"
cmp "$output.images.before.sha256" "$output.images.after.sha256"
printf 'Saved matched compiler rows to %s\n' "$output"
