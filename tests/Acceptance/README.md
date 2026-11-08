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
| Regex match | A Path uses its native text; fallback is necessary |
| Type match and `decltype` | Types select arms; inspecting an effectful expression creates no file |
| Generic functions | Calls specialize Path/String separately; callbacks carry actual functions |
| Recursive functions | Recursive calls execute with changing arguments, not infinite expansion |
| Scopes and connections | Lexical lookup is distinct from endpoint qualification |
| Literal and Path transfers | String source is exact UTF-8 data; explicit Path source reads a file |
| Relocatable/executable artifacts | Artifacts execute after deleting the source, without reparsing it |

The integration team's broader suite owns packaging, SSH/WSL transport, generic
CLI option acceptance and editor protocol. This suite targets language behavior
with file bytes as the observable user result.
