#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
if [[ $EUID != 0 ]] || [[ "$(readlink /proc/self/ns/mnt)" == "$(readlink /proc/1/ns/mnt)" ]]; then
    echo 'Run as root inside an explicitly private mount namespace (unshare --mount --fork).' >&2
    exit 2
fi
if [[ $# != 2 ]] || [[ -z ${MSYNC_PERF_USER:-} ]]; then
    echo 'Set MSYNC_PERF_USER and pass baseline and candidate release driver paths.' >&2
    exit 2
fi
baseline=$(realpath "$1")
candidate=$(realpath "$2")
test -x "$baseline"
test -x "$candidate"
mkdir -p .temp/perf-v4/ext4
mount --make-rprivate /
mount --bind /tmp "$PWD/.temp/perf-v4/ext4"
echo "# $(date -u +%FT%TZ) $(uname -r) user=$MSYNC_PERF_USER"
findmnt -T "$PWD/.temp/perf-v4/ext4" -o SOURCE,FSTYPE,TARGET

IFS=',' read -ra sizes <<< "${MSYNC_V4_PLANNER_SIZES:-10,50,100,200}"
index=0
for workload in independent forward-chain overwritten missing-overwritten; do
    if [[ -n ${MSYNC_V4_PLANNER_CASE:-} && $MSYNC_V4_PLANNER_CASE != "$workload" ]]; then
        continue
    fi
    for count in "${sizes[@]}"; do
        [[ $count =~ ^[1-9][0-9]*$ ]]
        if ((index % 2)); then variants=(candidate baseline); else variants=(baseline candidate); fi
        for variant in "${variants[@]}"; do
            driver=$baseline
            if [[ $variant == candidate ]]; then driver=$candidate; fi
            runuser -u "$MSYNC_PERF_USER" -- env \
                MSYNC_PERF_ROOT="$PWD/.temp/perf-v4/ext4" \
                MSYNC_PERF_CONTEXT_ROOT=/tmp \
                MSYNC_V4_PLANNER_CASE="$workload" \
                MSYNC_V4_PLANNER_SIZES="$count" \
                MSYNC_V4_PLANNER_SAMPLES="${MSYNC_V4_PLANNER_SAMPLES:-7}" \
                taskset -c "${MSYNC_PERF_CPU:-0}" "$driver" \
                | sed "s/^{/{\"variant\":\"$variant\",/"
        done
        index=$((index + 1))
    done
done
