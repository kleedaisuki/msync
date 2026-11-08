# Lexical scopes

A Scope groups named values. Members can be Paths, functions, nested Scopes or
other typed expressions. Use `::` to select a member.

```msync
scope MyClass {
    new: fn() -> Scope { scope { x: ~/.codex } },
    get: fn(some) { some::x },
};
let instance = MyClass::new();
let file = MyClass::get(instance);
```

The return value of `new()` is a Scope, so `get(instance)` can select `x`.
`MyClass::new()::x` is already a Path: passing it to `get` would make `some::x`
invalid. Pass the instance, or make a separate function that accepts a Path.

Scopes and functions can be anonymous and assigned to mutable bindings. Their
values may change, but their inferred or declared storage types remain fixed.

```msync
let settings = scope { root: ~/.codex, enabled: true };
let root = settings::root;
let alias = settings;
```

A Scope is lexical, not a machine or remote connection. `$wsl`, `$local` and
`ssh://forge` have type Connection. Read `msync skill paths connections` before
using the same `::` spelling to qualify a physical endpoint.

## Mutable fields and aliases

A Scope field accepts any inferred value type, including Connection. Field
membership and types are fixed; assignment changes an existing field's value.
Aliases refer to the same Scope instance, while each constructor call creates a
fresh instance.

```msync
scope FileState {
    remote: ssh://forge,
    new: fn() -> Scope { scope { filename?: null } },
};
let object = FileState::new();
let alias = object;
alias::filename = ~/.codex;
let file = object::filename;
```

A Null field initializer requires `?`: `filename: null` is an error.
`filename?: null` leaves its non-null payload type to compile-time inference;
the later Path store fixes it to Path with Null admitted, before execution.
Further stores must respect that fixed type. After a possibly overlapping write
or call, narrow a nullable field again before using its non-null capabilities.
