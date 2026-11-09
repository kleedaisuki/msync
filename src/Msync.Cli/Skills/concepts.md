# Named structural concepts

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

An associated type relates an algebra's identity and operations:

```msync
concept Monoid {
    type Self;
    identity: Self,
    combine: fn(left: Self, right: Self) -> Self,
};
scope TextConcat {
    Self: String,
    identity: "",
    combine: fn(left: String, right: String) -> String {
        $std::string::concat(left, right)
    },
};
fn combine_text(algebra: Monoid) -> String {
    algebra::combine(algebra::identity, "hello")
};
let combined = combine_text(TextConcat);
```

The compiler checks capabilities and type relations, not mathematical laws.
An operation named `combine` does not prove associativity or authorize a
reordered floating-point reduction. Select the algebra explicitly as a value.
