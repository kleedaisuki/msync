#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
git ls-files --error-unmatch Cargo.toml README.md >/dev/null || { echo 'Package from a Git checkout so only owned documents are shipped.' >&2; exit 1; }
version=$(sed -n 's/^version = "\([^"]*\)"/\1/p' Cargo.toml | head -1)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo "invalid package version: $version" >&2; exit 1; }
target=x86_64-unknown-linux-gnu
cargo build --locked --release --target "$target"
name="msync-$version-linux-x86_64"
target_root=${CARGO_TARGET_DIR:-"$root/target"}
[[ "$target_root" == /* ]] || target_root="$root/$target_root"
stage="$target_root/package/$name"
rm -rf -- "$stage"
mkdir -p "$stage/bin" "$stage/share/doc/msync" "$root/dist"
install -m755 "$target_root/$target/release/msync" "$stage/bin/msync"
cp README.md LICENSE "$stage/share/doc/msync/"
while IFS= read -r -d '' file; do
  install -Dm644 "$file" "$stage/share/doc/msync/$file"
done < <(git ls-files -z -- docs)
cp packaging/linux/INSTALL.md "$stage/INSTALL.md"
python3 packaging/licenses.py --target "$target" --output "$stage/share/doc/msync/THIRD-PARTY-NOTICES.txt"
[[ "$("$stage/bin/msync" --version)" == "msync $version" ]]
epoch=${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct)}
[[ "$epoch" =~ ^[0-9]+$ ]] || { echo 'SOURCE_DATE_EPOCH must contain Unix seconds' >&2; exit 1; }
tar --sort=name --mtime="@$epoch" --owner=0 --group=0 --numeric-owner -C "$target_root/package" -cf - "$name" | gzip -n > "$root/dist/$name.tar.gz"
(cd "$root/dist" && sha256sum "$name.tar.gz" > "$name-SHA256SUMS.txt")
printf '%s\n' "$root/dist/$name.tar.gz"
