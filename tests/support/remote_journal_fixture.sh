set -eu
if [ "${1:-}" = --helper ]; then
    lib=$2; kind=$3; shift 3
    [ "$(awk '/^CapEff:/ {print $2}' /proc/self/status)" = 0000000000000000 ] || { echo 'helper requires zero effective capabilities' >&2; exit 1; }
    group=$(ps -o pgid= -p $$ | tr -d ' ')
    [ "$group" = "$MSYNC_HELPER_PGID" ] || { echo 'helper escaped its isolated supervisor group' >&2; exit 1; }
    recovery=RECOVERY
    if [ "$kind" = RECOVER-restore-crash ]; then recovery=RECOVERY-restore-crash; kind=RECOVER; fi
    if [ "$kind" = RECOVER-gc-crash ]; then recovery=RECOVERY-gc-crash; kind=RECOVER; fi
    if [ "$kind" = SETTLE-gc-crash ]; then recovery=RECOVERY-gc-crash; kind=SETTLE; fi
    . "$lib/$recovery.sh"
    . "$lib/$kind.sh"
    exit 0
fi

lib=$1
root=$2
logs=$3
fixture=$0
[ "$(awk '/^CapEff:/ {print $2}' /proc/self/status)" = 0000000000000000 ] || { echo 'fixture requires zero effective capabilities to prove DAC behavior' >&2; exit 1; }
[ "$(dirname -- "$root")" = "$(dirname -- "$lib")" ]
[ ! -e "$root" ] && [ ! -L "$root" ]
mkdir "$root"
run_id=${root##*/}
trap 'status=$?; if [ "$status" = 0 ]; then find "$root" -type d -exec chmod u+rwx -- {} \;; rm -rf -- "$root"; else echo "failed journal fixture retained at $root" >&2; fi' EXIT
mkdir -p "$logs"
printf 'uid=%s capeff=%s filesystem=%s\n' "$(id -u)" "$(awk '/^CapEff:/ {print $2}' /proc/self/status)" "$(stat -f -c '%T:%t' -- "$root")"
sha256sum "$lib"/*.sh > "$logs/helper-hashes.sha256"

tree() (
    path=$1; value=$2; epoch=$3
    mkdir -p "$path/nested/empty"
    printf '%s\000payload\n' "$value" > "$path/nested/data"
    chmod 0400 "$path/nested/data"
    chmod 0500 "$path/nested/empty" "$path/nested" "$path"
    [ "$(stat -c %a -- "$path")" = 500 ] || { echo 'fixture filesystem does not expose Unix directory mode 0500' >&2; exit 1; }
    touch -d "@$epoch" "$path/nested/data" "$path/nested/empty" "$path/nested" "$path"
)
assert_tree() (
    path=$1; reference=$2; epoch=$3
    [ -d "$path/nested/empty" ]
    cmp "$path/nested/data" "$reference/nested/data"
    for p in '' nested nested/empty; do
        d=$path; [ -z "$p" ] || d=$d/$p
        [ "$(stat -c %a -- "$d")" = 500 ]
        [ "$(stat -c %Y -- "$d")" = "$epoch" ]
    done
    [ "$(stat -c %a -- "$path/nested/data")" = 400 ]
    [ "$(stat -c %Y -- "$path/nested/data")" = "$epoch" ]
)
invoke() {
    if setsid -f -w sh -c '
        export MSYNC_HELPER_PGID=$$
        exec timeout --kill-after=2s 20s sh "$@"
    ' sh "$fixture" --helper "$lib" "$@"; then return 0; else status=$?; fi
    # util-linux setsid --wait reports a killed child as signal 9 here, rather
    # than shell status 137. Normalize only a positively reached fault marker.
    if [ "$status" = 9 ] && [ -n "${MSYNC_TEST_KILL_MARKER:-}" ] && [ -s "$MSYNC_TEST_KILL_MARKER" ]; then return 137; fi
    return "$status"
}
allocate() {
    allocation=$(invoke ALLOCATE "$1" '' '')
    journal=$(printf '%s\n' "$allocation" | sed -n '1p')
    destination=$(printf '%s\n' "$allocation" | sed -n '2p')
    publication=$(printf '%s\n' "$allocation" | sed -n '3p')
    case $journal in "$root"/*/.msync-v4.*) ;; *) echo 'unsafe allocated journal' >&2; exit 1;; esac
    [ -d "$journal" ]
    cp -a --reflink=auto -- "$root/new-source" "$journal/payload"
}
assert_source() {
    assert_tree "$root/new-source" "$root/new-reference" 978307200
    [ "$(stat -c %a -- "$root/shared-source-file")" = 400 ]
    cmp "$root/shared-source-file" "$root/new-reference/nested/data"
}
assert_old_alias() {
    [ "$(stat -c %a -- "$1/external-old-link")" = 400 ]
    cmp "$1/external-old-link" "$root/old-reference/nested/data"
}
assert_clean() {
    [ -z "$(find "$1" -maxdepth 1 -name '.msync-v4.*' -print -quit)" ]
}

tree "$root/old-reference" old-generation 978307100
tree "$root/new-source" new-generation 978307200
cp -a --reflink=auto -- "$root/new-source" "$root/new-reference"
ln "$root/new-source/nested/data" "$root/shared-source-file"

while read -r label expected wanted; do
    case_root=$root/$label
    mkdir "$case_root"
    cp -a --reflink=auto -- "$root/old-reference" "$case_root/target"
    ln "$case_root/target/nested/data" "$case_root/external-old-link"
    allocate "$case_root/target"
    export MSYNC_TEST_KILL_MARKER=$logs/$run_id-$label.publish.kill
    if invoke "PUBLISH-$label" "$journal" "$destination" "$publication" replace single source > "$logs/$label.publish.stdout" 2> "$logs/$label.publish.stderr"; then code=0; else code=$?; fi
    [ "$code" = "$wanted" ] || { printf '%s unexpected publisher exit %s != %s\n' "$label" "$code" "$wanted" >&2; cat "$logs/$label.publish.stderr" >&2; exit 1; }
    invoke RECOVER "$journal" > "$logs/$label.recover.stdout" 2> "$logs/$label.recover.stderr"
    if [ "$expected" = old ]; then reference=$root/old-reference; epoch=978307100; else reference=$root/new-source; epoch=978307200; fi
    assert_tree "$case_root/target" "$reference" "$epoch"
    assert_old_alias "$case_root"
    assert_source
    invoke RECOVER "$journal" > "$logs/$label.repeat.stdout" 2> "$logs/$label.repeat.stderr"
    assert_tree "$case_root/target" "$reference" "$epoch"
    assert_old_alias "$case_root"
    assert_source
    assert_clean "$case_root"
    printf 'PASS %s result=%s root-mode=500 file-mode=400\n' "$label" "$expected"
done < "$lib/plan.txt"

case_root=$root/interrupted-recovery
mkdir "$case_root"
cp -a --reflink=auto -- "$root/old-reference" "$case_root/target"
ln "$case_root/target/nested/data" "$case_root/external-old-link"
allocate "$case_root/target"
export MSYNC_TEST_KILL_MARKER=$logs/$run_id-interrupted.publish.kill
if invoke PUBLISH-after-restore "$journal" "$destination" "$publication" replace single source > "$logs/interrupted.publish.stdout" 2> "$logs/interrupted.publish.stderr"; then code=0; else code=$?; fi
[ "$code" = 137 ]
export MSYNC_TEST_KILL_MARKER=$logs/$run_id-interrupted.recover.kill
if invoke RECOVER-restore-crash "$journal" > "$logs/interrupted.recover.stdout" 2> "$logs/interrupted.recover.stderr"; then code=0; else code=$?; fi
[ "$code" = 137 ]
[ -d "$case_root/target" ]
[ ! -e "$journal/before" ]
cmp "$case_root/target/nested/data" "$root/old-reference/nested/data"
invoke RECOVER "$journal" > "$logs/interrupted.finish.stdout" 2> "$logs/interrupted.finish.stderr"
assert_tree "$case_root/target" "$root/old-reference" 978307100
invoke RECOVER "$journal" > "$logs/interrupted.repeat.stdout" 2> "$logs/interrupted.repeat.stderr"
assert_tree "$case_root/target" "$root/old-reference" 978307100
assert_source
assert_old_alias "$case_root"
assert_clean "$case_root"
printf 'PASS interrupted-recovery restores old root mode=500 idempotently\n'

case_root=$root/missing-parent
mkdir "$case_root"
allocate "$case_root/absent/parent/target"
invoke PUBLISH-normal "$journal" "$destination" "$publication" replace single source > "$logs/missing.publish.stdout" 2> "$logs/missing.publish.stderr"
invoke RECOVER "$journal" > "$logs/missing.recover.stdout" 2> "$logs/missing.recover.stderr"
assert_tree "$case_root/absent/parent/target" "$root/new-source" 978307200
invoke RECOVER "$journal" > "$logs/missing.repeat.stdout" 2> "$logs/missing.repeat.stderr"
assert_tree "$case_root/absent/parent/target" "$root/new-source" 978307200
assert_source
assert_clean "$case_root"
printf 'PASS missing-parent private readonly payload promotion\n'

for entry in RECOVER SETTLE; do
    case_root=$root/gc-$entry
    mkdir "$case_root"
    cp -a --reflink=auto -- "$root/old-reference" "$case_root/target"
    ln "$case_root/target/nested/data" "$case_root/external-old-link"
    allocate "$case_root/target"
    invoke PUBLISH-normal "$journal" "$destination" "$publication" replace single source > "$logs/gc-$entry.publish.stdout" 2> "$logs/gc-$entry.publish.stderr"
    installed=$(stat -c '%d:%i' -- "$case_root/target")
    assert_tree "$case_root/target" "$root/new-reference" 978307200
    export MSYNC_TEST_KILL_MARKER=$logs/$run_id-gc-$entry.kill
    if invoke "$entry-gc-crash" "$journal" > "$logs/gc-$entry.cut.stdout" 2> "$logs/gc-$entry.cut.stderr"; then code=0; else code=$?; fi
    [ "$code" = 137 ]
    [ ! -e "$journal/intent" ]
    [ ! -e "$journal/COMMITTED" ]
    [ -d "$journal/before" ]
    [ ! -e "$journal/before/nested/data" ]
    assert_old_alias "$case_root"
    invoke RECOVER "$journal" > "$logs/gc-$entry.finish.stdout" 2> "$logs/gc-$entry.finish.stderr"
    assert_tree "$case_root/target" "$root/new-reference" 978307200
    [ "$(stat -c '%d:%i' -- "$case_root/target")" = "$installed" ]
    invoke RECOVER "$journal" > "$logs/gc-$entry.repeat.stdout" 2> "$logs/gc-$entry.repeat.stderr"
    assert_tree "$case_root/target" "$root/new-reference" 978307200
    assert_source
    assert_old_alias "$case_root"
    assert_clean "$case_root"
    printf 'PASS %s committed partial-GC crash after durable intent retirement\n' "$entry"
done

case_root=$root/gc-aborted
mkdir "$case_root"
cp -a --reflink=auto -- "$root/old-reference" "$case_root/target"
ln "$case_root/target/nested/data" "$case_root/external-old-link"
allocate "$case_root/target"
export MSYNC_TEST_KILL_MARKER=$logs/$run_id-gc-aborted.publish.kill
if invoke PUBLISH-after-old-admit "$journal" "$destination" "$publication" replace single source > "$logs/gc-aborted.publish.stdout" 2> "$logs/gc-aborted.publish.stderr"; then code=0; else code=$?; fi
[ "$code" = 137 ]
export MSYNC_TEST_KILL_MARKER=$logs/$run_id-gc-aborted.recover.kill
if invoke RECOVER-gc-crash "$journal" > "$logs/gc-aborted.cut.stdout" 2> "$logs/gc-aborted.cut.stderr"; then code=0; else code=$?; fi
[ "$code" = 137 ]
[ ! -e "$journal/intent" ]
[ ! -e "$journal/next/nested/data" ]
assert_tree "$case_root/target" "$root/old-reference" 978307100
invoke RECOVER "$journal" > "$logs/gc-aborted.finish.stdout" 2> "$logs/gc-aborted.finish.stderr"
invoke RECOVER "$journal" > "$logs/gc-aborted.repeat.stdout" 2> "$logs/gc-aborted.repeat.stderr"
assert_tree "$case_root/target" "$root/old-reference" 978307100
assert_source
assert_old_alias "$case_root"
assert_clean "$case_root"
printf 'PASS aborted partial-GC crash preserves settled old root mode=500\n'

case_root=$root/foreign-identity
mkdir "$case_root"
cp -a --reflink=auto -- "$root/old-reference" "$case_root/target"
ln "$case_root/target/nested/data" "$case_root/external-old-link"
allocate "$case_root/target"
export MSYNC_TEST_KILL_MARKER=$logs/$run_id-foreign.publish.kill
if invoke PUBLISH-after-restore "$journal" "$destination" "$publication" replace single source > "$logs/foreign.publish.stdout" 2> "$logs/foreign.publish.stderr"; then code=0; else code=$?; fi
[ "$code" = 137 ]
mv -T -- "$case_root/target" "$case_root/known-installed-root"
tree "$case_root/target" foreign-generation 978307300
foreign=$(stat -c '%d:%i' -- "$case_root/target")
if invoke RECOVER "$journal" > "$logs/foreign.recover.stdout" 2> "$logs/foreign.recover.stderr"; then echo 'foreign recovery unexpectedly succeeded' >&2; exit 1; else code=$?; fi
[ "$code" = 1 ]
[ "$(stat -c '%d:%i' -- "$case_root/target")" = "$foreign" ]
[ "$(stat -c %a -- "$case_root/target")" = 500 ]
[ "$(stat -c %Y -- "$case_root/target")" = 978307300 ]
printf 'foreign-generation\000payload\n' > "$case_root/foreign-reference"
cmp "$case_root/target/nested/data" "$case_root/foreign-reference"
assert_source
assert_old_alias "$case_root"
[ -d "$journal" ]
printf 'PASS foreign identity refused without chmod or overwrite\n'
printf 'PASS all readonly remote publication crash/recovery cases\n'
