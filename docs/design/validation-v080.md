# v0.8 independent user acceptance

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

The remaining package failure exposed an embedded-standard-library/compiler
revision mismatch: loading `ranges.msync` rejected `native fn of(value) ->
Range;` against the variadic `ranges.of` signature. The compiler source already
accepts the variadic declaration layout; a rebuilt package requires only the
failed callback journey to be repeated.

A separate first-class-function probe accepted a returned closure retaining its
captured argument, including both source-free artifact stages. Choosing between
two anonymous functions in a runtime `if` still failed with `Function value has
no inferred callable signature`; the concrete reproduction was sent to the
compiler owner. That is a missing callable-alternative model, not a reason to
restrict legal function-valued conditionals.
