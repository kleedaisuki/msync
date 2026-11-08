# Standard library: {{module}}

Select this module with `$std::{{module}}`. The declarations below are the
actual standard-library source embedded in this msync executable, shown in its
module-registration context rather than as standalone user declarations. Call
these operations through `$std::{{module}}::operation(...)`. Unannotated
functions specialize at calls; native Range operations refine element and
callback result types from their arguments.

```msync
{{source}}
```

A native declaration registers an existing host operation; it is not an FFI for
arbitrary dynamic libraries. Query names inspect values or metadata, whereas
mutations and native process calls take effect only when the program runs.
For Path/String parameters, preserve the value's endpoint and conversion intent.
