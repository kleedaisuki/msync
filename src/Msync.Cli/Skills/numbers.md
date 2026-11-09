# Numbers and arithmetic

`Number` is IEEE 754 binary64. Decimal and exponent notation, hexadecimal,
binary and octal integer notation, and digit separators are source literals.
Unary plus and minus are operators; minus preserves negative zero.

```msync
let decimal = 1_024.5;
let fraction = .5;
let exponent = 1.25e+3;
let hexadecimal = 0xff;
let binary = 0b1010;
let octal = 0o17;
let negative_zero = -0;
let result = (decimal + fraction) / 2 - 7 % 4;
let power = 2 ** 3 ** 2;
```

Use `+`, `-`, `*`, `/`, `%` and `**` for arithmetic. Parentheses make grouping
explicit. `/` also joins Path/String operands; the operand types select the
operation. Use explicit `./` or `~/` spelling for paths beginning with digits.
Binding names such as `copy-config` and `.codex` remain legal atom names.
Exponentiation groups to the right: `2 ** 3 ** 2` is 512. It binds more tightly
than a unary sign: `-2 ** 2` is -4, while `2 ** -2` is 0.25. `%` is the
truncating remainder, whose sign follows its left operand.

Division follows IEEE 754, including infinities and NaN rather than an integer
division exception. NaN is unequal to itself; ordered comparisons with NaN are
false. Use the explicit math classification operations when that case matters.

Binary64 does not represent every integer above 2^53 exactly. Integer count
parameters require a finite, integral value in their supported domain; adding
an underscore or using a hexadecimal spelling does not create an integer type.
Lazy ranges consume only requested items, even when their count is large.

`$std::math` supplies floating-point classification and numerical operations;
`msync skill library math` shows the executable declarations. Ordinary Number
equality and ordering are numerical operations, not a comparison of stored
bits. Do not assume floating-point addition is associative when folding values.
