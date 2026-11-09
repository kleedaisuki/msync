# Named Scope initialization and identity

A named `scope Name { ... };` declaration denotes one mutable Scope instance per
program execution or REPL execution session. Its initializer runs only on the
first selected execution of that declaration. Later executions return the same
instance, including its shared field cells and captured closures. An anonymous
`scope { ... }` still creates an independent instance on every execution.

```msync
fn cache(initial: String) {
    scope Shared { value: initial };
    Shared
};
let first = cache("initial");
first::value = "updated";
cache("ignored")::value // "updated"
```

The first *executed* initializer wins, not the first compile-time specialization.
An unselected branch cannot initialize the instance or run its effects. The
inferred field storage constraints remain fixed across specializations; a second
specialization does not create another singleton with incompatible field types.
Singleton reads therefore retain schema and callable information, but not the
initializer's literal-value or nullable-flow facts.

The compiler gives each Scope syntax identity a stable allocation-site number
within its Compilation. This identity is reused across function specializations
and remains distinct across REPL submissions. `GetSingleton` pushes either the
initialized Scope or Null; ordinary `Dup`, `IsNull` and branching skip the entire
initializer on a hit. `SetSingleton` consumes the candidate and returns the canonical published Scope.
If recursive initialization published an instance first, that instance wins; the
outer initializer returns the same identity rather than replacing it. Both operations have
observable memory effects and publication escapes the instance, so private
aggregate scalarization cannot turn it into per-call storage.

Standalone interpreters own their own lazily allocated table. REPL interpreters
share the table through their ExecutionSession's SessionImage, alongside the
shared code image. No process-wide registry, filesystem state, live credentials
or singleton payload is embedded into an artifact. Source, relocatable artifacts
and executable artifacts retain the same declaration identity. Separate sessions
never share these instances. Failed compiler submissions discard new semantic
singleton entries with their rolled-back function templates.

`tests/NamedScopes` exercises initializer side effects, captured field aliases,
first selected execution, anonymous freshness, session lifetime and session
isolation at O0/O2/O3 through source, `.mso` and `.mexe` round trips.
