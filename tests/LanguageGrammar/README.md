# Number, structural requirements and scope identity grammar

This focused parser journey covers numeric literal forms and precedence,
unary signs on bindings, exact arithmetic highlighting and completion spans,
Path/name discriminators, static casts, endpoint qualification, concept
requirements, and named versus anonymous Scope expressions.

Large base-prefixed literals are compared with an independent BigInteger to
binary64 conversion, including rounding-sensitive values above UInt64.

The final managed snapshot 4 passed the complete journey with zero build
warnings or errors. Only this test project was built:

```text
dotnet restore tests/LanguageGrammar --no-dependencies
dotnet build tests/LanguageGrammar --no-restore -p:BuildProjectReferences=false
dotnet run --project tests/LanguageGrammar --no-build --no-restore
```
