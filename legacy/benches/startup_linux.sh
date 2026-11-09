#!/bin/bash
set -euo pipefail

# Run from the repository root through unshare --mount --fork as root.
root=$(pwd -P)
[ -f "$root/Cargo.toml" ] && [ -f "$root/benches/startup.rs" ] || exit 2
baseline=$(realpath -e -- "${1:?provide the baseline executable}")
current=$(realpath -e -- "${2:?provide the current executable}")
samples=${3:-30}
native=${MSYNC_NATIVE_STORAGE:-/tmp}
base="$root/.temp/perf-v3/ext4"
mkdir -p "$base"
mount --make-rprivate /
mount --bind "$native" "$base"
work=$(mktemp -d "$base/msync-startup-XXXXXXXX")
cleanup() {
    cd "$root"
    case "$work" in "$base"/msync-startup-*) rm -rf -- "$work" ;; *) exit 1 ;; esac
    umount "$base"
}
trap cleanup EXIT
if [ "$(findmnt -n -o FSTYPE -T "$work")" != ext4 ]; then
    echo 'MSYNC_NATIVE_STORAGE must select native ext4, not tmpfs or DrvFs' >&2
    exit 2
fi
cp -- "$baseline" "$work/baseline"
cp -- "$current" "$work/current"
cp -- "$root/target/perf-probe/startup-linux" "$work/startup"
chmod 0755 "$work/baseline" "$work/current" "$work/startup"
cd "$work"
date --iso-8601=seconds
findmnt -T "$work" -o TARGET,SOURCE,FSTYPE,OPTIONS
stat -f -c 'startup-data filesystem=%T magic=%t' "$work/current"
./baseline --version
./current --version
taskset -c 0 ./startup ./baseline ./current "$samples"
