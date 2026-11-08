# Functions and specialization

`fn` is the callable construct. Omit parameter annotations for inference from
each call; add a type or union when a function requires specific capabilities.
The final expression is the result. Do not append `;` to a value you need back.

```msync
fn identity(value) { value };
fn copy(source: Path | String, destination: Path) {
    source -> destination
};
let replace = fn(file: Path, destination: Path) { file -> destination };
let same_path = identity(~/.codex);
let same_bool = identity(true);
```

String and Path have implicit conversion where a parameter or annotation asks
for it. This affects execution: a String converted to Path is read as a file.
An unconstrained String passed to `->` writes literal content instead.

Functions capture referenced lexical bindings by value. They can be returned,
passed to native callbacks, or called recursively.

```msync
fn countdown(value: Number) -> Bool {
    if value == 0 { true } else { countdown(value - 1) }
};
let flipped = $std::ranges::transform([true, false], fn(value) { not value });
let result = countdown(3);
```

A generic-body error is diagnosed at the failing expression with the concrete
argument types and the call site. Fix the argument's capabilities or the body;
an arbitrary type annotation does not turn a Path into a Scope or Connection.

A Path is callable too: `./tool("argument")` starts a native Process with exact
arguments, not shell interpolation. Read `msync skill library process` for
result inspection.
