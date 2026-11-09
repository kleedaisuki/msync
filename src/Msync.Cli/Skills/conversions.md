# Explicit conversion with as

Write `value as Type` to convert a value explicitly. The target is a source
type constraint, so the compiler infers the converted result before execution.
`as` is an operator, not a dynamically selected standard-library function.

```msync
let number = "42" as Number;
let text = number as String;
let file = "./report.txt" as Path;
let selected = ("1.5" as Number) + 0.5;
```

Only Path and String convert implicitly. Number/String/Bool conversion requires
`as`. String-to-Bool parses `true` or `false`; a nonempty String does not become
true automatically. Invalid selected conversions report their actual data and
target. A non-nullable target requires a nullable operand to be narrowed first.

Path/String conversion retains endpoint spelling and path provenance. Merely
creating or converting a Connection does not authenticate it. Typed Range
conversion converts demanded elements rather than eagerly consuming the source.
Compatible Scope, Function and Process conversions preserve identity.
