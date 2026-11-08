# Independent 0.8 acceptance

`language_journeys.py` runs the real CLI in isolated fixtures below the root
`.temp/acceptance-v080`. Assertions derive from the requested language, not
from compiler-internal nodes. It uses only the Python standard library.

```sh
python3 tests/Acceptance/language_journeys.py /absolute/path/to/msync
```

Use `--case NAME` to select the changed contract. A private frozen copy of the executable (and sibling managed assemblies when
needed), fixtures and JSON results are retained under `.temp` for diagnosis. Do not run the full selection repeatedly
when an unchanged contract has already passed.

| Contract | Distinguishing observation |
| --- | --- |
| Transfer failure result | A missing source yields false, executes the else branch, preserves the target and reports the actual cause |
| Brace blocks and semicolons | Tail returns a value; a trailing semicolon yields Null |
| Nullable declarations | Unmarked nullable binding is rejected; `?` tests is-null |
| Flow refinement | Both `if` branches and `match null` permit only admitted operations |
| Exhaustive match | Bool complete arms work; missing Bool/Null and regex-only matches fail |
| Regex match | Raw/native Path text is consistent with String conversion; Null/Bool select fallback; regex arms narrow nullable Paths |
| Type match and `decltype` | Types select compatible generic arms; inspecting an effectful expression creates no file |
| Generic functions | Calls specialize Path/String separately; callbacks carry actual functions |
| Recursive functions | Recursive calls execute with changing arguments, not infinite expansion |
| Scopes and connections | Branch/Range scope projections preserve member unions; lexical lookup is distinct from endpoint qualification |
| Ordinary builtin-named files | Loading `$std` does not hide physical `file`, `path`, `pwd` or `std` files |
| Literal and Path transfers | String source is exact UTF-8 data; explicit Path source reads a file |
| Scalar String order | Constant folding, runtime/captured comparison and directory iteration preserve Unicode scalar order |
| Full Unicode strings | Legacy full casing/FinalSigma/ligatures, regex and supplementary-plane filenames retain exact bytes |
| Relocatable/executable artifacts | Artifacts execute after deleting the source, without reparsing it |

The integration team's broader suite owns packaging, SSH/WSL transport, generic
CLI option acceptance and editor protocol. This suite targets language behavior
with file bytes as the observable user result.

The anchored local-relative regex fixture uses the host's native separator:
`./input.txt` on Unix and `.\\input.txt` on Windows (the regex escapes the
backslash). It still rejects absolute projections. The separate SSH-qualified
`~/input.txt` fixture keeps POSIX source spelling independently of the host.
