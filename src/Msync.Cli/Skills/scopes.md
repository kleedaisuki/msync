# Lexical scopes

A Scope groups named values. Members can be Paths, functions, nested Scopes or
other typed expressions. Use `::` to select a member.

```msync
scope MyClass {
    new: fn() { scope { x: ~/.codex } },
    get: fn(some) { some::x },
};
let instance = MyClass::new();
let file = MyClass::get(instance);
```

The return value of `new()` is a Scope, so `get(instance)` can select `x`.
`MyClass::new()::x` is already a Path: passing it to `get` would make `some::x`
invalid. Pass the instance, or make a separate function that accepts a Path.

Scopes and functions can be anonymous and assigned to immutable bindings.

```msync
let settings = scope { root: ~/.codex, enabled: true };
let root = settings::root;
let alias = settings;
```

A Scope is lexical, not a machine or remote connection. `$wsl`, `$local` and
`ssh://forge` have type Connection. Read `msync skill paths connections` before
using the same `::` spelling to qualify a physical endpoint.
