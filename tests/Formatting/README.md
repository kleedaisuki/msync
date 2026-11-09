# Syntax formatter contracts

Run `dotnet run --project tests/Formatting`. The harness compiles the product
syntax files directly, keeping formatting checks independent of runtime and
semantic compilation. It checks new Number/concept/cast/endpoint syntax, parse
round trips, idempotence, 80-column hanging layout, logical-chain readability,
declaration/comment paragraph separation, and 1,000 arbitrary UTF-16 String
payloads. The actual CLI formatting/execution journey remains
`tests/Msync.EndToEnd/formatter_journey.py`.

Layout follows [PEP 8](https://peps.python.org/pep-0008/) hanging indentation and
leading binary operators, with
[Black's outer-delimiter decomposition](https://black.readthedocs.io/en/stable/the_black_code_style/current_style.html).
The language retains its own braces and semicolons, a soft width of 80, and
unchanged literal lexemes. Deep structural indentation and indivisible literals
can exceed that width. Named atom spelling and raw paths are not rewritten into
arithmetic; ambiguous variable arithmetic still needs lexical separation.
