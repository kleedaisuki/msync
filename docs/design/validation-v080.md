# v0.8 independent user acceptance

## Final deployment boundary

The extracted Linux Native AOT executable has SHA-256
`ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b`.
The final implementation is C#; the independently buildable Rust 0.7.3 baseline
is retained under `legacy/`. Local strict solution compilation passes all eight
projects with zero warnings/errors, and solution whitespace verification passes.

The same frozen native payload passed six encrypted OpenSSH CLI journeys:
deferred selectors and recursive iterators with typed filesystem callbacks,
each through source, `.mso` and `.mexe`. Every transfer used one client,
authentication and exec session, with zero scp clients; compile/bind opened zero
clients. The result is `.temp/cli-transfer-ssh/s-vb71nwo7/cli-result.json`.

The runtime-independent deployment runner is
[`tests/Deployment/container_journey.py`](../../tests/Deployment/container_journey.py).
It mounts the extracted package, not a developer build directory, in a pinned
Ubuntu 22.04 base image without .NET or ICU. It checks compile/bind have no output
effects, removes source before both binary executions, runs `.mexe` from another
cwd, verifies exact Unicode bytes and native child stdout, and reads two actual
SQLite sessions. All observations passed with the frozen native executable.
The image is
`ubuntu:22.04@sha256:076e7bf82319619f28dd0aa9c02ab34b055ad37100395b4f49a2ffba04fb5e11`;
the retained result is `.temp/deployment-v080/native-z0lygwml/results.json`.

```sh
python3 tests/Deployment/container_journey.py /absolute/path/to/extracted/msync-package
```

The maintained runners preserve procedures and expected bytes beyond temporary
fixture cleanup. Linux package and VSIX identities are recorded in
[`packaging-v080.md`](packaging-v080.md). No public release/tag was created.
Windows Native AOT and installer workflows are configured; these local Linux
results are not presented as Windows execution evidence.

## Current accepted state

The final extracted Linux Native AOT package passes **16/16 focused changed-
contract journeys**, each through source, source-free `.mso`, and bound `.mexe`
execution. The earlier defects recorded below are
preserved as regression rationale; all identified language/artifact failures
have been corrected. Unchanged earlier successful contracts were not subjected
to another full sweep. See the final-package section for the frozen identity.

## Contract and ownership

The 0.8 rewrite changes source grammar, host implementation and artifact format
at once. Verification therefore follows a complete user operation rather than
counting parser/type-checker tests: write source, check it without effects,
execute it, compile binary relocatable IR, bind executable IR, remove source,
then execute those artifacts and compare actual output bytes.

The independent harness is [`tests/Acceptance/language_journeys.py`](../../tests/Acceptance/language_journeys.py).
Its contract inventory is in the adjacent README. Integration tests own broad
CLI, packaging and editor behavior. Runtime/transport tests own actual native
SSH process/authentication/channel counts and filesystem metadata preservation.
The preserved Rust tree is an executable 0.7 baseline, not a parser oracle for
the deliberately new 0.8 syntax.

## Expected semantic distinctions

- An operational transfer failure yields false and a source-owned diagnostic;
  a handled false may run later work while the CLI reports a failed attempt.
  Cancellation and compiler/bytecode faults are not ordinary false outcomes.
- `;` discards an expression value; a block with no tail value has type Null.
- `let value?` admits Null; unmarked bindings do not. `_` discards rather than
  retaining a nullable binding. `value?` is an is-null test, not a truthiness or
  non-null test.
- `null` participates in `is`/`is not`, not ordinary equality or Boolean
  operations. Refinement must permit the non-null branch while rejecting an
  operation on an unrefined nullable value.
- Match exhaustiveness depends on the inferred input type. Regex patterns do
  not prove completeness for arbitrary Strings or Paths. Type patterns can
  exhaust a finite union; wildcard covers the remaining cases.
- `decltype` infers without evaluating its operand, including when that operand
  is a transfer, filesystem observation or process call.
- String transfer sources remain literal bytes. Implicit String/Path conversion
  must not silently turn ordinary quoted data into an existence-dependent file
  read. An explicit Path value selects file contents.
- Relocatable and executable bytecode contain already-bound lexical operations,
  never debug source that the runtime reparses. An artifact still runs when its
  original source is unavailable.

## Execution record

The first managed CLI snapshot completed **33/34 journeys**, with 149 actual
CLI subprocess invocations, on Linux. The snapshot retained its managed sibling
assemblies under `.temp/acceptance-v080/cli-aggkel7d`; the entry apphost hash was
`10e48f20bebc980b777a13c12fb5f107c54b91d9e0cb47b5354ecc3b8cf2b282`.
The apphost alone is not a managed implementation identity; current harness
records additionally hash every frozen sibling file and the sorted manifest.

```sh
python3 tests/Acceptance/language_journeys.py \
  src/Msync.Cli/bin/Debug/net10.0/msync --artifacts \
  --output .temp/acceptance-v080/initial-results.json
```

Accepted behavior includes nullable control-flow refinement, exhaustive Bool/
finite-union matching, regex Path matching, actual Path/String conversion,
callbacks, anonymous scopes, literal bytes, unselected SSH branches, handled
transfer failure with preserved target/diagnostics, source-free `.mso`/`.mexe`,
relocated `.mso` cwd versus bound `.mexe` cwd, and fresh file bytes after binding.
`decltype` of a transfer or nonexistent process performed neither operation.

The sole failure is a real scanner/compiler integration gap: recursive
`countdown(value - 1)` classified subtraction as a Path, producing `Expected
Number, found Path` rather than executing recursion. The unchanged successful
journeys are accepted; correction will be checked by the focused recursion
selection rather than another full managed sweep. Native AOT execution remains
a separate target-runtime check.


An incremental two-journey run additionally accepted inferred recursive functions
without return annotations and a real source callback passed to the lazy native
`$std::ranges::filter`, then consumed by a file-copy loop. Each passed source,
source-free `.mso` and bound `.mexe` execution. This isolates the countdown
failure to numeric subtraction rather than recursive specialization itself.

```sh
python3 tests/Acceptance/language_journeys.py \
  src/Msync.Cli/bin/Debug/net10.0/msync --artifacts \
  --case inferred_recursive_function --case native_range_callback \
  --output .temp/acceptance-v080/incremental-recursion-callback.json
```

The frozen managed file-manifest SHA-256 for this incremental run is
`4111ddc2914333ebf32f7428b64cc31c46b71cd981e0d4c2d0a68858ba467726`.


A further two-journey incremental run accepted a real Path call returning a
Process (the child prints `msync --version`, and its stdout is copied exactly)
and the removed `async`, `await`, and `delete` spellings as ordinary lexical
names. Both passed source and both source-free artifact stages. The child uses
the same frozen CLI snapshot; no system-specific shell is required.

```sh
python3 tests/Acceptance/language_journeys.py \
  src/Msync.Cli/bin/Debug/net10.0/msync --artifacts \
  --case path_call_returns_real_process --case removed_keywords_are_ordinary_names \
  --output .temp/acceptance-v080/incremental-process-keywords.json
```

Frozen managed file-manifest SHA-256:
`4111ddc2914333ebf32f7428b64cc31c46b71cd981e0d4c2d0a68858ba467726`.


## Native AOT target-runtime selection

A focused nine-journey run against the actual Linux Native AOT package accepted
eight journeys, including the corrected countdown recursion. The selection
covered actual child Process execution, inferred recursion, regex matching,
artifact relocation/binding, Path/String parameter conversion, false transfer
results, and unevaluated transfer typing. This is target-runtime evidence, not
a repeat of every unchanged managed-language check.

```sh
python3 tests/Acceptance/language_journeys.py \
  artifacts/package/msync-0.8.0-linux-x86_64/bin/msync --artifacts \
  --case regex_path_and_fallback --case native_range_callback \
  --case inferred_recursive_function --case recursive_function \
  --case path_call_returns_real_process --case artifact_context_relocation \
  --case path_parameter_coerces_filename_string \
  --case transfer_false_runs_else_and_keeps_target \
  --case decltype_transfer_does_not_execute \
  --output .temp/acceptance-v080/native-aot-target-results.json
```

Frozen package file-manifest SHA-256:
`db53241818655591412d2ce767ff153c3675f3f15871dc25678f439ce981af16`.
The harness preserves adjacent native dependencies such as `libe_sqlite3.so`.

The initial package failure exposed an embedded-standard-library/compiler
revision mismatch: loading `ranges.msync` rejected `native fn of(value) ->
Range;` against the variadic `ranges.of` signature. The compiler source already
accepts the variadic declaration layout; a rebuilt package requires only the
failed callback journey to be repeated.

A separate first-class-function probe accepted a returned closure retaining its
captured argument, including both source-free artifact stages. Choosing between
two anonymous functions in a runtime `if` initially failed with `Function value has
no inferred callable signature`; the concrete reproduction was sent to the
compiler owner. The implementation added a callable-alternative dispatch model rather than
restricting legal function-valued conditionals.


## Corrected regressions and verifier/block-driver integration

Anchored tests first demonstrated the Path regex bug against a retained old
snapshot: `./input.txt` failed `<^[.]/input[.]txt$>`, and SSH-qualified
`remote::~/input.txt` failed `<^~/input[.]txt$>`. Regex matching had projected
Paths to absolute local filenames instead of using the same raw/native text
conversion as explicit Path-to-String conversion. Neither regex test performs
remote I/O. Both now pass, as do both selections between anonymous functions.

After the bytecode verifier and block-driven runtime changed, the focused
12-journey selection passed source, source-free `.mso`, and bound `.mexe`:

- nullable present/absent `?`, `is not null`, and residual `match null` narrowing;
- both runtime anonymous-function alternatives;
- both anchored raw Path regex cases;
- Scope member Path/String unions with either runtime branch selected;
- Range<Scope> source/target member projection;
- real `file`, `path`, `pwd`, and `std` filenames, with both bare and `./`
  spellings after `$std::fs` is accessed.

The CLI was built with isolated artifacts (no concurrent canonical obj/bin
writes), strict diagnostics, and zero warnings/errors:

```sh
dotnet build src/Msync.Cli/Msync.Cli.csproj \
  --artifacts-path .temp/acceptance-v080/final-build -m:1 -nr:false
```

The exact selected test names and subprocess commands are retained in
`.temp/acceptance-v080/final-validator-scope-journeys.json`.
Frozen managed file-manifest SHA-256:
`2ae40f4febfd7751ef3527e1ea4b12d41496c238b85fca2a110cfb3fba891afa`.
This selection replaces only the affected earlier conclusions; no unchanged
full managed sweep was repeated.


## Type-dependent generic control and partial regex predicates

A generic function may dispatch on `decltype(value)` and use incompatible
operations in different type-specialized branches: Path transfer in one branch,
Boolean negation in the other. A focused source/`.mso`/`.mexe` journey accepted
both `if` and exhaustive type `match` forms, calling each function with a Path
and with both Boolean values. The latter distinguishes per-type specialization
from accidentally freezing the first runtime Boolean value.
Report: `.temp/acceptance-v080/generic-type-dispatch.json`.

Regex patterns are partial text predicates, not implicit stringification for
all types. A Path/Null scrutinee narrows to Path in the regex arm; actual Null
and Bool values select the fallback. Mixed-Bool fallback and malformed-regex
compile diagnostics passed immediately. The first nullable source journeys ran
correctly but artifact emission rejected their unreachable Null transfer arms.
The verifier now propagates RegexMatch's known-false result for known nontext
inputs; both nullable present/absent cases pass all three stages.
Report: `.temp/acceptance-v080/regex-nullable-verifier-corrected.json`;
frozen managed manifest `df3c789899e8c619faa630d0b9122b78dc4a791d926ebba53a0d2025f2be56b7`.

## Full Unicode compatibility, independent of ICU

The expected casing bytes were obtained from the actual preserved Rust 0.7.3
CLI, whose `string.to_lowercase`/`to_uppercase` use Rust's full Unicode casing.
The independent journey covers dotted/dotless I, sharp S, contextual Greek
FinalSigma across combining marks/apostrophes/ZWJ, Deseret supplementary-plane
letters, Latin and Armenian ligature expansions, Unicode regex matching and an
actual supplementary-plane filename.

A retained pre-fix C# image demonstrably failed the expected lowercase bytes:
.NET simple casing did not preserve full expansion or FinalSigma behavior.
The official Unicode-table implementation now passes the exact legacy-derived
expectations through source, source-free `.mso`, and bound `.mexe` execution.
Globalization-invariant AOT configuration must not silently redefine language
string semantics; ICU/default equivalence alone was never compatibility evidence.

```sh
python3 tests/Acceptance/language_journeys.py \
  .temp/acceptance-v080/final-build/bin/Msync.Cli/debug/msync --artifacts \
  --case full_unicode_case_regex_and_native_filename \
  --output .temp/acceptance-v080/unicode-full-casing-corrected.json
```

The actual Rust probe is retained below `.temp/acceptance-v080/unicode-rust-baseline`;
its useful expected bytes are persistent in the harness, so fixture cleanup does
not lose the regression. Corrected managed manifest SHA-256:
`df3c789899e8c619faa630d0b9122b78dc4a791d926ebba53a0d2025f2be56b7`.


## Scalar ordering and final packaged acceptance

An independent old/new CLI comparison exposed UTF-16 ordering as a compatibility
regression: U+10000 must sort after U+E000, matching Rust's UTF-8/scalar order.
Two persistent journeys first failed the old C# image and now pass the final
Native AOT image:

1. Constant expressions, typed runtime function parameters, and a closure
   capturing a runtime-selected String all use scalar ordering. The parameter
   check includes a common prefix whose last UTF-16 code unit is a shared high
   surrogate (U+10001 versus U+10000), so an optimized comparison must not decode
   a low surrogate as a new scalar.
2. Native directory iteration visits U+E000 before U+10000. Files are created in
   the opposite order; sequential overwrites observe the actual iteration order
   rather than merely asserting that both names were enumerated.

The final extracted-package selection passed all **16** journeys:

| Changed contract | Journeys |
| --- | ---: |
| Native range callback / variadic source declaration | 1 |
| Nullable `?`, `is not`, residual match artifact validation | 4 |
| Both runtime function alternatives | 2 |
| Anchored raw Path and partial nontext regex matching | 5 |
| Full Unicode casing, regex, native filename | 1 |
| Generic type-dependent incompatible branches | 1 |
| Scalar comparison and native directory order | 2 |

Frozen executable SHA-256:
`ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b`.
Frozen executable plus native-dependency manifest SHA-256:
`413c858b40ca599e085cec35179183a3d56439f70045b9f2ec6e27fd5959cf15`.
The image was taken from the actual extracted package, not a managed surrogate.
Every selected successful program was checked without effects, executed from
source, compiled and bound without effects, had its source removed, and then
executed from both binary stages with exact file-byte assertions.

The exact selection is recorded in
`.temp/acceptance-v080/final-native-changed-contracts.json`, with the focused
`is not null` artifact extension in
`.temp/acceptance-v080/final-native-is-not-artifacts.json`. That existing case
initially exercised source only; its artifact flag was corrected, and only that
case was rerun against the identical executable/dependency manifest. The other
fifteen accepted journeys were not repeated. The selection is reproducible
using `tests/Acceptance/language_journeys.py BINARY --artifacts` with the sixteen
case names listed by that report. The former stale-variadic package failure is
resolved in this actual image, as are nullable-regex verifier false rejections.
Ruff, strict mypy, and strict isolated C# builds passed for the final harness and
its managed validation inputs. No selected acceptance failure remains open.
