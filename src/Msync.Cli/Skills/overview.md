---
name: msync
description: Write, inspect and run typed cross-endpoint file workflows in msync.
---

# msync 0.8 skill

Use msync for local, native SSH and Windows/WSL file workflows. Programs are
`.msync` source; they compose typed Paths, Connections, Scopes and functions.
This reference is embedded in the executable and matches its language version.

## Working method

1. Write an expression-based program using braces and `let` bindings.
2. Run `msync check job.msync` to catch syntax and type errors without effects.
3. Run `msync plan job.msync` to inspect bytecode and scheduled basic blocks.
4. Preview with `msync run --dry-run job.msync`; a preview is not a transfer.
5. Run `msync run job.msync` when ready. Read the actual operation result.

```msync
fn copy(source: Path, destination: Path) { source -> destination };
let copied = copy(./input.txt, ./backup.txt);
```

## Semantics to retain

- `{ expression }` returns a value; `;` discards it and yields Null.
- Assignment changes binding and Scope field values, never their inferred types.
- Later `let` declarations shadow earlier locations; closures retain locations.
- A Path reads file content in `->`; a String writes its literal text.
- `Scope::member` selects a lexical value; `path @ connection` selects an endpoint.
- Admit Null explicitly, then handle it. `value?` means **is null**.
- Functions are generic by default; parameters specialize from each call.
- Named Scopes are shared singletons; anonymous Scopes construct fresh values.
- Concepts constrain structure without adding runtime types; `as` converts.
- Checking, formatting and reading this skill never execute the program.

Read only the relevant child topic rather than requesting every detail at once.
For example: `msync skill language functions` or `msync skill workflow artifacts`.
For command options, use `msync --help` or `msync help COMMAND`.
