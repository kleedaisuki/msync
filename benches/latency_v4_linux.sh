#!/bin/bash
# Native data and loader measurements; namespace setup is outside the timer.
set -euo pipefail

if [[ ${MSYNC_LATENCY_PRIVATE_NS:-0} != 1 ]]; then
    [[ $(id -u) == 0 ]] || { echo 'Run this private-mount runner as WSL root.' >&2; exit 1; }
    exec unshare --mount --fork env MSYNC_LATENCY_PRIVATE_NS=1 bash "$0" "$@"
fi

[[ $# -ge 2 ]] || { echo 'usage: latency_v4_linux.sh BASELINE CANDIDATE|- [SAMPLES] [native|project] [USER]' >&2; exit 1; }
cd "$(dirname "$0")/.."
mount --make-rprivate /
mkdir -p .temp/perf-v4/ext4
mount --bind /tmp "$PWD/.temp/perf-v4/ext4"

baseline=$(realpath "$1")
candidate=$2
[[ $candidate == - ]] || candidate=$(realpath "$candidate")
samples=${3:-15}
address=${4:-native}
user=${5:-$(id -nu 1000)}
home=$(getent passwd "$user" | cut -d: -f6)
toolchain=$(runuser -u "$user" -- env RUSTUP_HOME="$home/.rustup" "$home/.cargo/bin/rustc" --version)
extra=()
case $address in
    native) extra=(/tmp) ;;
    project) ;;
    *) echo "Unknown pathname control: $address" >&2; exit 1 ;;
esac

printf 'environment,runner_uid,%s\n' "$(id -u)"
printf 'environment,measurement_user,%s\n' "$user"
printf 'environment,toolchain,%s\n' "$toolchain"
printf 'environment,kernel,%s\n' "$(uname -r)"
findmnt -T "$PWD/.temp/perf-v4/ext4" -n -o SOURCE,FSTYPE
stat -f -c 'environment,fixture_fs,%T magic=%t' "$PWD/.temp/perf-v4/ext4"
exec runuser -u "$user" -- taskset -c 0 target/perf-probe/latency-v4 \
    "$baseline" "$candidate" "$PWD/.temp/perf-v4/ext4" "$samples" "${extra[@]}"
