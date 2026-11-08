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
Runtime owner reports this correction is on disk; focused rerun is pending.

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
has exact fixtures and is implementing the correction.

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
Compiler owner has the reproducer and is implementing the correction.

### P1: Remote Path calls do not resolve home-relative executables

`RunProcessAsync` builds a remote command by quoting `executable.Raw` directly.
For `(remote::~/tool)()`, the resulting `'~/tool'` does not expand remote HOME,
so a valid executable returns exit 127. The initial fixture observed empty
stdout instead of `REMOTE OK`. The separate process SSH builder also omits the
explicit SSH configuration/URI-port handling used by transfer calls.

Remedy: share native SSH client construction and endpoint path expansion while
preserving exact argv. A shell-executing SSH shim establishes command/argument
behavior; it is deliberately not reported as real authentication evidence.
The regression also checks configured `-F` and URI `-p` arguments. Runtime owner
is integrating the shared launcher with the transport owner.

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

Pending integration of the four directly reported findings.
