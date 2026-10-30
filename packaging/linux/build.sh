#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
version=$(sed -n 's/^version = "\([^"]*\)"/\1/p' Cargo.toml | head -1)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo "invalid package version: $version" >&2; exit 1; }
target=x86_64-unknown-linux-gnu
cargo build --locked --release --target "$target"
name="msync-$version-linux-x86_64"
stage="$root/target/package/$name"
rm -rf -- "$stage"
mkdir -p "$stage/bin" "$stage/share/doc/msync" "$root/dist"
install -m755 "target/$target/release/msync" "$stage/bin/msync"
cp README.md LICENSE "$stage/share/doc/msync/"
cp -R docs "$stage/share/doc/msync/"
cp packaging/linux/INSTALL.md "$stage/INSTALL.md"
python3 packaging/licenses.py --target "$target" --output "$stage/share/doc/msync/THIRD-PARTY-NOTICES.txt"
[[ "$("$stage/bin/msync" --version)" == "msync $version" ]]
epoch=$(git log -1 --format=%ct)
tar --sort=name --mtime="@$epoch" --owner=0 --group=0 --numeric-owner -C "$root/target/package" -cf - "$name" | gzip -n > "$root/dist/$name.tar.gz"
(cd "$root/dist" && sha256sum "$name.tar.gz" > "$name-SHA256SUMS.txt")
printf '%s\n' "$root/dist/$name.tar.gz"
