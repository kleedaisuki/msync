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

Implementation is in progress. The next record will include the frozen CLI
identity, exact command selection and per-journey observations; a test file's
presence alone is not a passed acceptance result.
