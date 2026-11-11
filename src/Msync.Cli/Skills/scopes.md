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

Named Scope declarations describe shared singletons. Reaching the declaration
again reuses that named instance. Anonymous `scope { ... }` expressions create
fresh instances each time they execute, including in a constructor function.
Both forms can be assigned to mutable bindings; storage types remain fixed.

```msync
let settings = scope { root: ~/.codex, enabled: true };
let root = settings::root;
let alias = settings;
```

A Scope is lexical, not a machine or remote connection. `$wsl`, `$local` and
`ssh://forge` have type Connection. Read `msync skill paths connections` before
using `connection @ path` to select a physical endpoint.

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

## Generic families and policies

The standard library uses named Scopes for constructors and associated
operations. Instances are anonymous data Scopes; operations receive the
instance explicitly. A policy is another Scope checked by a concept and
specialized at compile time:

```msync
scope IgnoreCase {
    Self: String,
    equal: fn(left: String, right: String) -> Bool {
        $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
    },
};
let names = $std::containers::EqualitySet::with_equality(["Klee"], IgnoreCase);
$std::containers::EqualitySet::insert(names, "KLEE");
```

`IgnoreCase` satisfies `$std::concepts::Equality`. The EqualitySet specializes
against that policy rather than attaching a method-closure table to each
instance. `let alias = names` shares one mutable instance; a new constructor
call owns separate state. Class names use PascalCase; associated function
names use lowercase words joined with underscores.
