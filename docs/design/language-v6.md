# Version 0.6 frontend grammar

This note records the frontend contract. [Integration](integration-v0.6.md) owns
cross-phase invariants and [ranges](ranges-v6.md) owns the supported adaptor
inventory. The parser does not inspect files or decide inferred types.

## Source forms

```text
enabled = true
label = "klee"
boolean any(a, b, c):
    return a or b or c

if enabled and not label == "disabled":
    delete "old.txt"
else if exists input.txt:
    input.txt -> output.txt
else:
    "empty" -> output.txt

for file in inputs/<.+> | views::take(1):
    file -> selected/

message = loop:
    break "complete"
drop enabled, any
```

`=` has its own `BindingOp::Assign`; existing `:=` and `=>` remain distinct.
The compiler enforces the assignment's type domain. `Boolean`, `Delete`, `Drop`,
`If`, `For`, `Loop` and `Break` are explicit statement variants rather than
magic calls. A loop-valued binding has an `ExprKind::Loop` body. `else if` is
represented as one `If` in the preceding branch's `else_body`.

Macro bodies may contain nested macro/symbol/boolean declarations. Symbol bodies
may contain symbols and boolean bodies may contain booleans; the compiler checks
these context restrictions and mandatory returns. The parser only owns valid
indentation, headers and AST structure. All block headers participate in REPL
compound-submission buffering; a blank line or EOF submits the whole block.

## Expression precedence

From weakest to strongest:

| Level | Operators |
| --- | --- |
| 1 | `or` |
| 2 | `and` |
| 3 | prefix `not` |
| 4 | range pipeline `\|` |
| 5 | `==`, `!=` |
| 6 | `<`, `<=`, `>`, `>=` |
| 7 | prefix `exists` |
| 8 | existing path composition `/`, selection `::`, calls and grouping |

Operators at a binary level associate left to right. Comparison chains are
ordinary typed binary expressions, not Python-style implicit conjunctions.
`not "a" == "b"` means `not ("a" == "b")`;
`files/<.+> | ranges::empty or true` combines the reducer's boolean result with
`true`. Parentheses can make either grouping explicit. Canonical formatting
parenthesizes binary expressions and unary operands, preserving AST meaning
without relying on a reader's memorized precedence.

The `not` ordering follows the familiar
[Python expression rule](https://docs.python.org/3/reference/expressions.html#operator-precedence),
while pipeline application follows the
[C++ adaptor-closure model](https://www.open-std.org/jtc1/sc22/wg21/docs/papers/2023/n4950.pdf)
of applying the right-hand closure to a range. This is a small, typed DSL, not a
promise to accept all Python expressions or all C++ range element domains.

## Token boundaries and compatibility

- An angle bracket opens a regex selector at a path-atom boundary, such as
  `root/<pattern>`, not between comparison operands. Quotes, escapes and regex
  character classes retain their existing shielding of operators and comments.
- `and` and `or` require token-delimited operator positions (whitespace or
  parentheses); `not(false)` and `exists(input)` are also accepted. Path components
  in `source/or/and/not` remain path atoms; `source=1 -> output=2` remains a transfer
  between filenames, not an assignment. Literal/qualified quoted paths remain the
  unambiguous way to use reserved spellings as filenames.
- Grouped expressions are not mistaken for empty-callee calls. Call arguments
  recursively support the same boolean and pipeline expressions.
- Byte spans remain owned by the original UTF-8 input. Added operators have
  their own spans; quoted operands retain decoded-byte provenance.
- Syntax defaults to `0.6`; `0.5` and patch-version spellings remain aliases of
  the compatible structured frontend. Removed `0.3` and transactional `0.4` are
  still rejected rather than silently reinterpreted.
- Parser nesting is bounded at 128 for blocks, calls, unary chains and operators;
  malformed input yields source reports rather than unbounded recursive parsing.

## Incremental frontend evidence

`tests/language_v6.rs` covers precedence, owned UTF-8 spans, nested declarations,
loop values, all REPL header classes, selector/filename ambiguity and malformed
control flow. `tests/language_presentation_v6.rs` covers new formatting,
highlighting, version aliases and property-based formatting idempotence.

During concurrent compiler development an actual-source harness under
`.temp/frontend-v6-harness` compiled the real language, syntax and highlight
modules with only a no-op telemetry stub. Focused frontend targets passed:
`language_v6` (6), `language_presentation_v6` (4), `language_v052` (9),
`language_v4` (12), `highlight` (3), and `version_v4` (5). Legacy normalizer
helpers were extended for AST variants, and the obsolete nested-macro rejection
case was removed because nested macros are now part of the requested grammar.
These results establish frontend behavior only; real CLI execution is recorded
in the integration validation notes. Six README DSL examples and three 0.6
release-note examples also parsed and formatted idempotently in that harness.
