# Language essentials

Braces delimit blocks; indentation is presentation rather than grammar. Each
block returns its final expression. A trailing `;` discards that result.
Declarations require `;`. Scope members and match arms are comma-separated.

```msync
let input = ~/.codex;
let ready = exists input;
let selected? = if ready { input } else { null };
if selected is not null { selected -> ./backup };
```

`let` is an immutable lexical binding, not assignment. A new declaration may
shadow an earlier name. Use `_` to discard a value deliberately.

Binding names are not limited to a conventional letter/underscore identifier.
For example, `.codex` is a legal name. A reference to an existing binding uses
that value; only an unbound bare name falls back to a filesystem Path.

```msync
let enabled = true;
let enabled = not enabled;
let _ = [true, false];
let .codex = ~/.codex;
let selected_config = .codex;
```

A bare unbound name denotes a Path, not an undefined-variable error. Spell
intentional filesystem values clearly (`./input.txt`, `~/config`) and inspect a
hover or `decltype` when a misspelling unexpectedly becomes a Path.

Strings use doubled quotes for an embedded quote. Backslashes and actual
newlines remain literal text; C-style escape sequences are not interpreted.

```msync
let text = "say ""hello""";
let backslashes = "C:\Users\Klee";
```

`if`, `for`, `match`, anonymous `fn` and anonymous `scope` are expressions.
Conditions require Bool; empty blocks and declarations produce Null.
