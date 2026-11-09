# Explicit typed conversion

Use `$std::cast(value, target)` with a Type value as the target. The target
determines the inferred result, so later operations and editor hints see the
converted type rather than an untyped wrapper.

```msync
let number = $std::cast("42", Number);
let text = $std::cast(number, String);
let file = $std::cast("./report.txt", Path);
let same_kind = $std::cast(file, decltype(./example.txt));
```

Number/String/Bool conversions are explicit. String-to-Bool parses `true` or
`false`; a nonempty String is not automatically true. Invalid selected text
conversions report an error. Null is accepted only by Null or a nullable target,
and nullable values must be handled before conversion to a non-nullable target.

Path/String conversion preserves the selected endpoint's spelling semantics.
Creating or inspecting a Connection value does not authenticate it. Casting a
Range to a typed Range converts its demanded elements without eagerly consuming
the source. Compatible Scope, Function and Process casts preserve identity.

## Executable declaration

This is the root declaration embedded in this executable, not a parallel list:

```msync
{{source}}
```
