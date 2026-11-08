# Independent 0.8 runtime and staging review

The review follows the requested 0.8 semantics and the retained ordinary-copy
model. It inspects actual bytecode lowering and runtime behavior rather than
using the old grammar as an oracle for the deliberately changed language.

## Reproducible findings

The maintained reproducer is
[`tests/Review/runtime_journeys.py`](../../tests/Review/runtime_journeys.py).
It freezes the CLI distribution, checks real output bytes, and optionally runs
the same operation from source-free `.mso` and `.mexe` artifacts. Select only
changed/previously failing contracts with `--case`; logs and fixtures live under
the repository-root `.temp/review-v080`.

### P1: Path joining removes ordinary POSIX backslash characters

`Interpreter`'s `PathJoin` opcode and `ExecutionLowering` independently apply
`TrimEnd('/', '\\')` / `TrimStart('/', '\\')` to every path. Backslash is a
separator only for Windows-local paths, not POSIX paths or SSH/WSL paths on a
Windows host. With directories named `left\\` and `left`, joining
`"./left\\" / "leaf"` silently selects the second directory's file. The initial
frozen CLI copied the decoy bytes `wrong` instead of `correct`.

Remedy: one shared endpoint/platform-aware path-join operation across the
interpreter and executable lowering. Preserve all nonseparator characters.
The shared join correction passed the focused source and artifact rerun below.

### P1: Function return annotations do not convert the returned value

`Compilation.Specialize` admits implicit Path/String conversion, then replaces
the result's static type without emitting the corresponding conversion.
`fn file() -> Path { "./input" }` therefore transfers the literal filename text
instead of reading the file. Conversely `-> String { ./input }` transfers file
bytes rather than the path's text. This is not merely a wrong diagnostic: the
declared semantic type selects the wrong user-visible transfer behavior.

The explicit-return variant additionally rejects a valid function:
`fn file() -> Path { return "./input"; }` is inferred as String? because an
unreachable discard-only block tail contributes Null. A terminal return should
not have a reachable fallthrough value.

Remedy: convert both tail and explicit-return paths to the declared result
type, and account for terminal control flow in block inference. Compiler owner
corrected both conversion paths and terminal control-flow inference; the focused
source and artifact rerun below passed.

### P1: Recursive closure calls capture caller-local shadows

`EmitClosure` rebuilds recursive self closures by looking up each capture's name
in the current environment. That environment can contain a local shadow rather
than the definition-time binding. The following program writes `inner`:

```text
let x = "outer";
fn recurse(done: Bool) -> String {
    if done { x } else { let x = "inner"; recurse(true) }
};
recurse(false) -> ./out;
```

The requested lexical closure semantics require `outer`. Capture ownership
must use retained slots or a retained self closure, not caller-name lookup.
Compiler owner corrected original capture-slot ownership; the focused source
and artifact rerun below passed.

### P1: Remote Path calls do not resolve home-relative executables

`RunProcessAsync` builds a remote command by quoting `executable.Raw` directly.
For `(remote::~/tool)()`, the resulting `'~/tool'` does not expand remote HOME,
so a valid executable returns exit 127. The initial fixture observed empty
stdout instead of `REMOTE OK`. The separate process SSH builder also omits the
explicit SSH configuration/URI-port handling used by transfer calls.

Remedy: share native SSH client construction and endpoint path expansion while
preserving exact argv. A shell-executing SSH shim establishes command/argument
behavior; it is deliberately not reported as real authentication evidence.
The regression also checks configured `-F` and URI `-p` arguments. The shared
launcher correction passed the focused source and artifact rerun below.

### P1: Remote tree replacement keeps the `/.` marker as a pathname

The shell upload/copy/relay paths pass the source-level destination marker `/.`
unchanged into `rm -rf "$target"`. GNU rm refuses to remove a directory spelled
with a final `.` component. Further, deriving `${target%/*}` from that spelling
places the owned stage *inside* the directory selected for removal. A normal
`./source -> remote::"/absolute/target/."` fails instead of replacing old target
children. The native shell reproduced the exact `refusing to remove '.' or '..'
directory` diagnostic.

Remedy: resolve the marker to the selected directory before deriving its parent
and placing the owned stage, then apply replacement to that actual directory.
The transport owner is integrating the correction.

### P2: Same-endpoint remote directory snapshots include their new stage

`CopyScript` stages a remote same-endpoint source with `cp -a "$source"
"$stage/payload"`, after creating the stage under the destination parent. For a
destination inside source, the stage is itself now inside source; native cp
rejects copying the directory into itself. Local transfers have an overlap-only
snapshot path, so this is also an endpoint inconsistency. The reproducer uses a
finite source directory and a `source/backup/.` destination, not a symlink cycle.

Remedy: capture the selected source before creating visible destination work, or
place this overlap-only owned snapshot outside the selected source. Do not put
snapshots on disjoint normal copies or prohibit ordinary overlapping trees.
The transport owner is integrating the correction.

### P1: Implicit remote selection authenticates metadata separately

`(remote::"/absolute/source" / <.+>) -> ./out/;` is one implicit finite Range
transfer. `SelectAsync` first asks the remote filesystem native for an iterator;
enumerating it opens an SSH process. `TransferEngine` then opens another native
SSH process for the selected payload. The initial CLI copied the correct `a`
and `b` bytes but launched two clients instead of one. This violates the
authoritative transfer admission model: metadata and bytes for one selected
finite transfer belong to one native SSH channel. The existing direct runtime
cohort fixtures supply already-enumerated Path values, so do not cover this
source-level selector journey.

Remedy: preserve a selected endpoint query in the range model until transfer
admission, and exchange enumeration, selection and payload on that one live
channel. Keep the language's regex semantics rather than translating it
unconditionally to an incompatible server regex dialect. Explicit `for` loops
retain their separate iteration semantics. The runtime owner has this finding.
The `remote-selector` review case asserts both copied bytes and exact native
client launches from source and source-free artifacts.

## Initial evidence and scope

The initial frozen managed CLI distribution was copied under
`.temp/review-v080/binary`; its host executable SHA-256 was
`10e48f20bebc980b777a13c12fb5f107c54b91d9e0cb47b5354ecc3b8cf2b282`.
Managed DLLs were frozen alongside the host because hashing the apphost alone
does not identify a managed implementation. The four focused journeys fail on
that image, as expected; output is retained in
`.temp/review-v080/regressions-3n7anc4g/results.json`.

The reproducer passes Ruff formatting/lint and strict mypy. These static checks
do not establish that the production defects are fixed. Final native-image
rerun results belong below after the owners integrate the changes.

The current local overlap snapshot mechanism addresses source/target ancestor
relationships without putting snapshots on ordinary disjoint file copies.
No new finding is raised merely for retaining completed members after failure,
for the absence of transactional recovery, or for lacking hostile-input quotas:
those are not requirements of the normal-user transfer model.

## Focused correction verification

The compiler-owner's managed CLI build at
`.temp/build-compiler/bin/Msync.Cli/debug/msync` passed the following targeted
review commands after the owners' corrections:

```sh
python3 tests/Review/runtime_journeys.py BINARY --artifacts \
  --case annotated-return --case recursive-capture
python3 tests/Review/runtime_journeys.py BINARY --artifacts \
  --case posix-backslash --case remote-process
```

These establish real source, `.mso`, and `.mexe` execution, including tail and
explicit return conversions in both directions, original recursive capture
ownership, both leading/trailing POSIX backslash preservation, remote HOME
executable resolution and explicit SSH configuration/URI port argv. The SSH shim
executes a real native shell but does not claim authentication or session-count
coverage. Exact result records are
`.temp/review-v080/regressions-rzn8u62g/results.json` and
`.temp/review-v080/regressions-0v_r1prd/results.json`.

The same fresh managed image still fails the two remote-tree cases, which have
been reported directly to the transport/runtime owners. Those cases and a final
Native AOT acceptance remain pending. The harness now records a distribution
digest in addition to the apphost hash so managed DLL changes are identifiable.

The `lazy-no-demand` case additionally passed source, `.mso`, and `.mexe` on a
fresh compiler-owner managed build (distribution SHA-256
`25717a0e05a22703c932c38e5649a7438bd508680b612eee5f97009d7c368832`).
It independently observes that `take(0)` does not evaluate an effectful
range-returning function, an effectful argument to that function, or an effectful
literal range item. All three marker files remain absent; the retained result
is `.temp/review-v080/regressions-5scnzp94/results.json`.
