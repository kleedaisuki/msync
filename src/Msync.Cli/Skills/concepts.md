# Named structural concepts

The standard library supplies reusable data-composition and capability
contracts. You do not need to redeclare an Option before accepting one:

```msync
fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? {
    value
};
let selected? = preserve($std::data::Option::some("Klee"));
let message? = match selected {
    null => { null },
    _ => { selected::value },
};
```

Read `msync skill library concepts` for supplied product, sum and capability
contracts, and `msync skill library data` for optional, product and sum values.
Concept names belong in type constraints and concept bases, not runtime value
expressions. `$std::concepts::Option` has no object to construct or register.
An absent Option is the language's `null`; a present value is an ordinary Scope.
`some(null)` is therefore present, not the same value as absence.

A `concept` describes the members and operation signatures required from an
existing value. It does not add a runtime type, registration table or `impl`
block. A Scope satisfies the requirement through its actual structure.

```msync
concept HasName {
    name: String,
};
concept NamedAction {
    HasName,
    run: fn(value: String) -> String,
};
fn apply(action: NamedAction, value: String) -> String {
    action::run(value)
};
scope Echo {
    name: "echo",
    run: fn(value: String) -> String { value },
};
let message = apply(Echo, "hello");
```

Use a concept wherever a parameter or result needs its structural constraint.
Concepts compose by including another concept in the body. Requirements are
checked at specialization, retaining the concrete member and callable shapes.
Missing members and incompatible signatures are errors at the selected value,
not runtime duck-typing failures.

Declare associated types inline where a structural requirement introduces
them. Each name is inferred from the actual field; later requirements can use
the same name:

```msync
concept Product {
    first?: typename First,
    second?: typename Second,
};
concept Sequence {
    items: Range<typename Item>,
    accepts: fn(value: Item) -> Bool,
};
```

The standard `$std::concepts::Product` already supplies the first contract;
the declaration above explains its syntax rather than requiring a local copy.
`typename` introduces an associated name, not a new runtime type. Ordinary `fn`
and `scope` specialize data operations and policies using the concrete shapes.
For a custom requirement, include a standard concept as a base and add only the
capabilities specific to your domain.

The compiler checks fields, signatures and type relations. It does not prove
mathematical laws or treat an untyped Function field as a higher-kinded type.
