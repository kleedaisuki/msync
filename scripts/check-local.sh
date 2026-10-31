#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/check-local.sh [--test NAME]... [--full] [--editor] [--packaging]

No options: check Rust formatting, strict Clippy, and lib/bin unit tests.
  --test NAME  Check and run only named Rust integration targets (repeatable).
  --full       Explicitly check/test all Rust targets plus documentation tests.
  --editor     Install locked npm dependencies and run editor unit tests.
  --packaging  Run publisher Node tests and existing Python license fixtures.
  --help       Show this help without running tools.

Editor/packaging-only selections do not invoke Cargo. Combine them with --test
or --full to include Rust. --full and --test are mutually exclusive.
Requires Bash (Windows users can use Git Bash). PYTHON overrides the interpreter
path for the existing packaging license tests; it is not a command fragment.
EOF
}

fail() { printf '%s\n' "$1" >&2; exit 2; }

tests=()
full=false
editor=false
packaging=false
while (($#)); do
  case "$1" in
    --help|-h) usage; exit 0 ;;
    --test)
      (($# >= 2)) || fail '--test requires a target name'
      [[ -n $2 && $2 != -* ]] || fail '--test requires a target name'
      tests+=(--test "$2")
      shift 2 ;;
    --full) full=true; shift ;;
    --editor) editor=true; shift ;;
    --packaging) packaging=true; shift ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
done
$full && ((${#tests[@]})) && fail 'Choose --full or --test, not both'

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
if $full || ((${#tests[@]})) || { ! $editor && ! $packaging; }; then
  printf '%s\n' 'Local Rust checks'
  cargo fmt --check
  targets=(--lib --bin msync)
  if $full; then targets=(--all-targets); else targets+=("${tests[@]}"); fi
  cargo clippy --locked "${targets[@]}" -- -D warnings
  if $full; then
    cargo test --locked --all-targets
    cargo test --locked --doc
  elif ((${#tests[@]})); then
    cargo test --locked "${tests[@]}"
  else
    cargo test --locked --lib --bin msync
  fi
fi
if $editor; then
  printf '%s\n' 'Local editor unit tests'
  (cd editors/vscode && npm ci --ignore-scripts && npm test)
fi
if $packaging; then
  printf '%s\n' 'Local packaging unit tests'
  node --test packaging/test-publish-release.cjs
  "${PYTHON:-python3}" -B -m unittest discover -s packaging -p test_licenses.py
fi
