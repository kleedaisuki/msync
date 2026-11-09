# Typed semantic HIR

Typed HIR is the semantic program between source analysis and bytecode lowering.
It records types, declaration identities, fixed storage constraints, generic
definitions, specialized functions, capture modes and structured control flow.
It has no VM stack slots or branch offsets.

## Export before lowering

```text
msync compile sync.msync --emit hir -o sync.mhir
```

The output extension `.mhir` selects HIR when `--emit` is absent. Explicit
`--emit bytecode` selects the existing relocatable bytecode stage. Without `-o`,
the command prints the selected representation; `-o -` emits binary stdout.
Compilation does not execute source effects or authenticate remote endpoints.

## Inspect without execution

```text
msync disassemble sync.mhir
msync disassemble sync.mhir --json
```

The view identifies sources, types, logical locations, functions, templates and
semantic nodes. `value` and `location` captures are distinct. Source text is
diagnostic data, never a script to replay. VS Code opens `.mhir` with the same
read-only navigation and refresh behavior as the other compiled artifact stages.

## Continue from the semantic file

```text
msync compile sync.mhir -O2 -o sync.mso
msync bind-ir sync.mhir -o sync.mexe
msync run sync.mhir
```

The original source can be removed. Lowering consumes the semantic tables rather
than invoking the source binder. `.mso` remains machine-independent bytecode;
`.mexe` retains its explicit binding context. A standalone `.mhir` does not
capture cwd, filesystem observations, credentials or live Process handles.

Session artifacts explicitly list unresolved locations if they require accepted
REPL state. Such a file is inspectable, but standalone compilation or execution
cannot invent the missing values. Initialized standard-library module locations
are not unresolved session imports.

HIR format version, bytecode format version, language semantic version and
product version are separate. HIR cannot be reconstructed from optimized `.mso`
or `.mexe`; export it from the original source before bytecode lowering.
