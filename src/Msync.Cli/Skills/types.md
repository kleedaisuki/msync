# Types and Null

Types are inferred. Optional annotations express requirements: Bool, String,
Path, Function, Scope, Connection, Process, Null, Number, Type and Range<T>.
Use `Path | String` for a union and `?` to admit Null explicitly.
`Any` admits any supported value type, including Null; use a narrower
constraint when an operation needs specific capabilities.

Parameters use `name: Type`; functions use `-> Type` for their result. Omitting
either constraint leaves that part inferred. For instance:

```msync
fn select_path(file: Path) -> Path { file };
let selected_path = select_path(~/.codex);
```

```msync
let input: Path = ~/.codex;
let available: Bool = exists input;
let file? = if available { input } else { null };
if file is null {
    // No transfer in the Null case.
} else {
    file -> ./backup
};
```

Null is a value, not false. Ordinary conditions, boolean operations and equality
comparisons do not accept it. Test with `is null` or `is not null` and handle
all cases before applying a non-null operation.

Postfix `file?` means **file is null**, not file is present. The `else` branch
of `if file? { ... } else { ... }` therefore has the non-null Path.

```msync
let kind = decltype(~/.codex);
let is_path = if kind is Path { true } else { false };
```

`decltype(expression)` infers a Type value without executing the expression.
It can select type-dependent branches during generic specialization. An
annotation does not grant unrelated capabilities: Path/String conversion is
supported, while Scope and Connection remain distinct semantic types.
