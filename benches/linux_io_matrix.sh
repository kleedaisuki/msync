#!/bin/bash
set -euo pipefail

# Run as root through `unshare --mount --fork` from the repository root.
# Bind native ext4 into the project instead of mistaking /mnt/d for Linux disk.
root=$(pwd -P)
if [ ! -f "$root/Cargo.toml" ] || [ ! -f "$root/benches/linux_io_probe.c" ]; then
    echo 'run from the msync repository root' >&2
    exit 2
fi
phase=${1:-full}
case "$phase" in selfcheck|pilot|full) ;; *) exit 2 ;; esac
base="$root/.temp/perf-v3"
mkdir -p "$base/ext4" "$base/tmpfs" "$base/results"
mount --make-rprivate /
mount --bind /tmp "$base/ext4"
mount -t tmpfs -o size=768m,mode=0700 msync-io-matrix "$base/tmpfs"
ext4=$(mktemp -d "$base/ext4/msync-matrix-XXXXXXXX")
memory=$(mktemp -d "$base/tmpfs/msync-matrix-XXXXXXXX")
drvfs=$(mktemp -d "$base/msync-drvfs-matrix-XXXXXXXX")
cleanup() {
    for directory in "$ext4" "$memory" "$drvfs"; do
        case "$directory" in "$base"/*/msync-matrix-*|"$base"/msync-drvfs-matrix-*) ;; *) exit 1 ;; esac
        rm -f -- "$directory/matrix-source-v2.bin" "$directory"/matrix-output-{0,1,2,3}.bin
        rmdir -- "$directory"
    done
    umount "$base/tmpfs"
    umount "$base/ext4"
}
trap cleanup EXIT
backends=(buffered vector range ring fixed)
filesystems=(ext4 tmpfs drvfs)
samples=3
mib=128
if [ "$phase" = selfcheck ]; then
    samples=1
    mib=1
    filesystems=(ext4)
elif [ "$phase" = pilot ]; then
    samples=1
    filesystems=(ext4)
fi
environment="$base/results/matrix-$phase-environment.txt"
{
    date --iso-8601=seconds
    uname -a
    findmnt -T "$ext4" -o TARGET,SOURCE,FSTYPE,OPTIONS
    findmnt -T "$memory" -o TARGET,SOURCE,FSTYPE,OPTIONS
    findmnt -T "$drvfs" -o TARGET,SOURCE,FSTYPE,OPTIONS
    gcc --version | head -1
    pkg-config --modversion liburing
    printf 'cpus=0-3; CHUNK=65536; payload=%sMiB; samples=%s; cache=warm,evict\n' "$mib" "$samples"
    printf 'evict means fsync+per-file FADV_DONTNEED; host cache is uncontrolled\n'
} > "$environment"
result="$base/results/matrix-$phase.jsonl"
: > "$result"
for filesystem in "${filesystems[@]}"; do
    case "$filesystem" in ext4) directory=$ext4 ;; tmpfs) directory=$memory ;; drvfs) directory=$drvfs ;; esac
    depths=(1 4 8 16)
    if [ "$filesystem" != ext4 ] || [ "$phase" = pilot ]; then depths=(1 16); fi
    for ((sample=0; sample<samples; sample++)); do
        for cache in warm evict; do
            for flows in 1 4; do
                for depth in "${depths[@]}"; do
                    # Rotate method order; all allocation/setup stays inside copy_ms.
                    for ((index=0; index<${#backends[@]}; index++)); do
                        backend=${backends[$(((index+sample+depth)%${#backends[@]}))]}
                        printf '%s fs=%s sample=%s cache=%s flows=%s qd=%s backend=%s\n' \
                            "$(date --iso-8601=seconds)" "$filesystem" "$sample" "$cache" "$flows" "$depth" "$backend" >&2
                        taskset -c 0-3 "$root/target/perf-probe/linux_io_probe" --matrix \
                            "$directory" "$filesystem" "$mib" "$cache" "$flows" "$depth" "$backend" 1 \
                            | sed "s/\"sample\":0/\"sample\":$sample/" >> "$result"
                    done
                done
            done
        done
    done
done
printf 'completed %s verified samples: %s\n' "$(wc -l < "$result")" "$result" >&2
