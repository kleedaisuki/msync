# Functions and specialization

`fn` is the callable construct. Omit parameter annotations for inference from
each call; add a type or union when a function requires specific capabilities.
The final expression is the result. Do not append `;` to a value you need back.
Write `-> Type` after the parameter list to constrain the returned value. This
works for both named and anonymous functions; it is separate from the `:` used
on a parameter.

```msync
fn identity(value) { value };
fn copy(source: Path | String, destination: Path) -> Bool {
    source -> destination
};
let replace = fn(file: Path, destination: Path) -> Bool {
    file -> destination
};
let same_path = identity(~/.codex);
let same_bool = identity(true);
```

String and Path have implicit conversion where a parameter or annotation asks
for it. This affects execution: a String converted to Path is read as a file.
An unconstrained String passed to `->` writes literal content instead.

Return constraints apply to both block tails and explicit `return` expressions.
Path/String conversion follows the declared result type, while unrelated types
are rejected when the function is specialized. Use a nullable result type to
admit Null deliberately:

```msync
fn locate(file: Path) -> Path? {
    if exists file { file } else { null }
};
let location? = locate(~/.codex);
```

For example, `fn bad() -> Bool { "text" }; bad();` is a type error. Adding a
semicolon after a non-Null block tail also changes the result to Null; it does
not return the discarded value.
An explicit `return value;` still returns immediately: the semicolon terminates
the statement without discarding the value that has already left the function.

Functions capture referenced lexical locations, not snapshots of their values.
Assignment to a captured location is visible to its closures; a later shadowing
declaration creates a different location. Functions can be returned, passed to
native callbacks, or called recursively.

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

## Explicit ancestor imports

`global` explicitly imports prior definition-time ancestor binding locations
inside a function. It does not declare process-wide variables. Existing implicit
captures remain valid; use an import when you want that dependency written out.
The nearest ancestor wins, not the caller's environment or a future declaration.

```msync
let root = ./before;
let .codex = ~/.codex;
fn redirect() {
    global root, .codex;
    root = .codex;
};
redirect();
```

The imported locations share assignments and retain their fixed storage types.
A later `let root = ...` shadows the old location without retargeting `redirect`.
`global` is invalid at top level, and cannot replace a parameter or an already
introduced local binding. Repeating the same import is allowed. A nested block
may shadow an imported name; leaving the block restores that imported name.
Scope bodies contain fields, not statements; a function-valued Scope field may
import its prior lexical ancestor fields with the same rules.
