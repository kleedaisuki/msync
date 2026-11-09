# Standard library: {{module}}

Use `$std::{{module}}::Name` in a parameter, return annotation, binding
constraint or concept base. These declarations are compile-time structural
requirements over existing values, not runtime Scope fields or new types.
The source below is embedded in the compiler and used for editor navigation.

```msync
{{source}}
```

The compiler checks required fields, operation signatures and associated-type
relations. Product and sum compositions use ordinary Scope records and
functions; optional values use the language's existing Null semantics.
Laws in the source comments are not automatically proved and do not authorize
optimization reorderings.

Read `msync skill library data` for Option, Pair, Result and Either composition,
or `msync skill library containers` for generic container families and policies.
