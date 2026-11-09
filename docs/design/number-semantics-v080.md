# Number completeness and algebra domains

## Implemented language contract (November 2026 upgrade)

Number remains the single closed IEEE binary64 carrier. Ordinary automation does
not need a public hierarchy of integer widths or an allocating arbitrary-precision
number representation. Decimal fractions, scientific notation, leading/trailing
decimal points, digit separators and binary/octal/hex integer spellings are
accepted by the shared `NumberSyntax` parser. Signed zero is preserved by unary
negation. Explicit Path spelling (`./1.5`, `~/1+2`) remains path data.

Source arithmetic follows the familiar ECMAScript binary64 model: numeric `/`
performs IEEE division, including infinity/NaN; `%` is truncating remainder with
the dividend's sign, not Python's divisor-sign modulo or IEEE nearest-quotient
remainder. `$std::math::remainder` remains the distinct IEEE remainder operation.
Multiplicative operators have equal precedence and associate left to right.
This follows the [ECMAScript Number operations](https://262.ecma-international.org/16.0/index.html#sec-ecmascript-language-types-number-type),
while literal grouping/base spellings follow the
[Python lexical grammar](https://docs.python.org/3/reference/lexical_analysis.html#numeric-literals).

Source numerical equality and ordered comparison now use IEEE semantics:
NaN is unequal to every Number, including itself; all ordered comparisons with
NaN are false; positive and negative zero compare equal. Runtime and optimizer
constant folding share `NumberSemantics.Compare`. This is not bit identity:
constant interning and dataflow still preserve exact bits. Range sorting retains
a total ordering that positions NaN before ordinary numbers; hash-based unique
and membership retain value/container identity semantics rather than claiming
to be source `==` operators. Concepts do not prove associativity or permit
binary64 reassociation.

Explicit `as Number` accepts invariant numeric text, including the same bases
and separators as literals, with optional sign and surrounding whitespace.
`NaN`/`Infinity` text retains the existing host parsing behavior. Explicit
`as Bool` maps zero and NaN to false and other Numbers to true; strings still
require the text `true` or `false` rather than implicit nonempty truthiness.
Only Path/String conversion is implicit.

`$std::math::is_integer` tests finite integral Numbers; `is_safe_integer` also
requires magnitude at most 2^53-1. The named values `nan`, `infinity`, `epsilon`
(distance from 1 to the next greater binary64 value), `max_safe_integer`,
`min_positive` (smallest positive subnormal), and `max_value` make the carrier's
bounds discoverable without requiring users to construct host-specific text.

Lazy range counts and indices (`take`, `drop`, `stride`, `step`, `repeat`, `at`,
`chunk`, `slide`) use one finite, integral, nonnegative Int64-domain contract;
stride/chunk/slide require positive counts. Actual representable Number values
below 2^63 are admitted; there is no arbitrary Int32 collection quota. String
counts are not silently parsed. Materializing collections retain their actual
CLR storage limits. `take`/`drop`/`stride` keep a remaining counter rather than an
ever-growing index, avoiding overflow on long streams without adding per-item
validation. Numeric sequence labels retain finite-number/progress semantics;
`enumerate_from` is not silently restricted to integral starts.

`tests/NumberSemantics` covers source and artifact execution at O0/O2/O3,
NaN/signed-zero distinctions, casts, wide lazy counts, invalid discrete inputs
and generated finite sequence models. The historical investigation below records
the baseline defects that motivated this change; its observations are not the
new language contract.

## Current implementation: evidence rather than inferred intent

| Layer | Actual implementation | Consequence |
| --- | --- | --- |
| Values | `src/Msync.Core/Values.cs`: `NumberValue(double Data)` | Every Number is binary64, including parsed integer text and host integral results |
| Source literals | `Syntax/Parser.cs`, line 330: `long.TryParse` then `NumberExpr` | Decimal/scientific literals are not parsed as numbers; even an integer beyond signed 64-bit falls back to a name/Path |
| Lexing | `Syntax/Lexer.cs`: `+`, `-`, `*`, `%` can remain inside an Atom | `1+2` is a Path while `1 + 2` is arithmetic; the DSL deliberately also admits atom-shaped names and paths |
| Source arithmetic | `Compiler/Values.cs`: numeric `+`, `-`, `*`, unary `-` | Numeric division exists in the VM but source `/` always lowers to PathJoin |
| Precedence | `Syntax/Parser.cs`: `*` at 8, `/` at 11 | Adding numeric `/` requires a precedence decision, not only a new branch in lowering |
| Math | `stdlib/math.msync` and `Runtime/NativeMath.cs` | 43 host binary64 operations plus source utilities already exist, including divide, pow, fmod, IEEE remainder, fma and classification |
| Comparisons | `Runtime/Interpreter.cs`: `double.CompareTo` | Source comparison implements a totalized host ordering: NaN equals NaN and sorts below non-NaN |
| Representation | `Core/ValueRepresentation.cs` | Constant pooling and dataflow equality use bit-exact doubles; signed-zero/NaN payloads are not merged merely because source comparison equates them |
| Artifact | `Core/Artifact.cs`: value tag 3 + `ReadDouble`/`Write(double)` | Formats 1/2/3 describe binary64 values; exact integer payloads cannot be silently added to that tag |
| Text conversion | `Runtime/NativeCast.cs` | Explicit Number casts accept invariant decimal/exponent text through `double.TryParse`; this capability is broader than source literal parsing |
| Discrete consumers | `NativeRanges.cs`, `NativeRanges.Extended.cs` | Legacy take/drop/stride use Int32 counts; repeat/chunk/slide/at use Int64 ordinals; both still receive binary64 Numbers |

The current language note calls Number a count primitive, whereas the expanded
math and cast libraries treat it as a full floating-point carrier. That mismatch
is now a product design issue, not merely an absent native math function.

## Focused actual Native observations

The control executable is the accepted stdlib Native AOT image from source
`e85e6170598dfd38adefbcb778e43647686c3096`, run `42603680953`:

* Binary: `artifacts/ci/42603680953/linux/installed/msync-0.8.0-linux-x86_64/bin/msync`.
* SHA-256: `2b2f40a00e3767fad638b30c096017de671ee1434a81984b9a8218e4587b2a4f`.
* Reproduction script and JSON: `.temp/number-semantics/probe.py` and
  `.temp/number-semantics/probe.json`.
* Each small expression runs through the actual redirected REPL; there are no
  timing claims or synthetic substitutions for the parser/runtime.

| Expression | Observed result |
| --- | --- |
| `decltype(12)` | `Number` |
| `decltype(1.5)`, `decltype(1e3)`, `decltype(.5)` | `Path` |
| `decltype(0x10)`, `decltype(1_000)` | `Path` |
| `decltype(9223372036854775808)` | `Path`, not a numeric overflow diagnostic |
| `decltype(1+2)`, `decltype(2*3)` | `Path` |
| `1 + 2` | `3` |
| `1 / 2` | E0203: expected String/Path, found Number |
| `7 % 4` | Path text `7 % 4`, not a remainder or unsupported-operator diagnostic |
| `+ 1` | Syntax error |
| `math::signbit(-0)` | `false` |
| `math::signbit(- 0)` | `true` |
| `cast("1.5", Number)`, `cast("1e3", Number)` | `1.5`, `1000` |
| `9007199254740993 == 9007199254740992` | `true` |
| `n == n`, `n < 0`, where `n = math::divide(0, 0)` | Both `true` |
| `cast(n, Bool)` for that NaN | `true`: explicit numeric nonzero conversion, not implicit truthiness |
| `math::fmod(7, 4)` / `math::remainder(7, 4)` | `3` / `-1` |

Here `math` and `cast` abbreviate `$std::math` and `$std::cast` in the table.
The `-0` discrepancy is specifically integer-text parsing followed by conversion
to double; unary negation and the math runtime already retain negative zero.

Selected finite-domain probes establish both limitations and working behavior:

* `size(take([], 2147483648))` fails at Int32 count validation even though the
  result would be empty. `first(repeat(1, 2147483648))` succeeds immediately.
  The asymmetry is an implementation-width contract, not a Number type rule.
* A sequence starting at `9007199254740992` with step 1 detects failure to
  advance on its next demand. `enumerate_from` likewise detects a stalled index;
  it must not be described as silently looping forever.
* `enumerate_from([1,2], cast("0.5",Number))` produces indices 0.5 and 1.5.
  Decide whether this is a generalized numeric label factory or should instead
  be an ordinal API; today it is not an integral-only enumeration domain.
* `cast("1e400",Number)` yields Infinity, following the host parser's range
  behavior. An explicit finite parser would be a different operation.

## Completion is three separate decisions

### 1. Source surface

The immediate gap is not another trigonometric function. It is a coherent route
from ordinary source text to the operations already supported by the runtime.

* Specify invariant decimal and scientific literals, digit separators, signed
  zero, range failures and malformed numeric diagnostics. Hex/binary integer
  spellings are useful but need an exactness policy before being advertised as
  bit manipulation support.
* Expose numeric division with well-defined precedence and associativity;
  distinguish truncating remainder (`fmod`) from IEEE remainder and Euclidean
  modulo. Do not call all three `%` without choosing one meaning.
* Unary plus, explicit parse/format facilities, finite/integral/safe-integral
  predicates and named constants (`nan`, `infinity`, `epsilon`, limits) should
  form a small, discoverable Number surface.
* The existing whitespace-sensitive atom grammar is also used for legal names
  such as `copy-config` and filenames. A global split-on-`-` lexer patch would
  break that contract. Numeric tokenization must preserve explicit path contexts
  (`./1.5`, `~/1+2`, remote paths) and quoted Path segments.
* Reserving previously path-shaped bare atoms such as `1.5` is a source semantic
  change. Make the grammar policy explicit and test real path scripts; old
  binary constants must retain their original Path/Number tags. Research should
  not disguise this as a harmless one-line parser change.

A minimally disruptive first implementation can retain the current `math`
functions while specifying the numeric lexical/operator extension separately.
If `/` becomes a closed built-in overload, dispatch by operand semantic types
(`Number,Number` versus Path/String), not by filename spelling or execution
probing. Numeric `/` must not accidentally retain the old tighter-than-`*`
parse tree; explicit grouping and Path `?` precedence also need preservation.

### 2. Discrete domains without opening the type universe

Counts, indices, byte values, signal numbers and PIDs are not arbitrary real
numbers. Keep these as named contracts over existing types when they are
refinements, rather than introducing a new public type for each host argument.

| Domain | Required contract |
| --- | --- |
| Count / index | Finite, integral, nonnegative; operation-specific representability |
| Positive stride / chunk size | Count plus nonzero |
| Byte | Exact integer in 0..255 |
| PID / host signal | Actual platform value and selected endpoint semantics, not a guessed proxy identity |
| Sequence step | Finite nonzero; progress checked where binary64 addition can stall |
| Finite scalar | No NaN or infinity; rounding still exists |

Unify lazy sequence counters on an appropriate wide iterator representation;
keep Int32 limits only where an actual CLR array/string materialization requires
them. Do not add a language-wide collection quota. Conversely do not convert a
rounded binary64 value back to Int64 and claim the user's original integer was
preserved. Binary64 has contiguous exact integers through 2^53, but safe adjacent
integer identity is conventionally bounded at 2^53-1; sparse larger even integers
are also representable and must not be confused with complete integer coverage.

### 3. Carrier representation

| Choice | Benefit | Cost / incompatibility |
| --- | --- | --- |
| Keep binary64 Number; complete syntax and contracts | Smallest change; existing math, opcode folding and artifact payload remain coherent | Cannot represent all 64-bit sizes/identifiers exactly; must not promise an exact unbounded integer algebra |
| One closed Number with internal exact-integer / binary64 alternatives | Exact counts and integer arithmetic without user-defined types or a numeric type zoo; small-int fast path with BigInteger only when needed is possible | Every numeric operation, equality/hash/order/cast and optimizer needs representation-aware rules; literals and mixed operations need a specification; new integer artifact tag/format capability required |
| Add a separate closed built-in Integer | Clearest carrier distinction and algebra contracts; precise APIs can demand Integer | Larger public type surface, conversions and literal inference; conflicts with the desire to avoid unnecessary type proliferation, though it is not an open type system |

Do not choose tagged Number merely as an invisible performance optimization.
`9007199254740992 + 1` becoming a distinct exact integer changes observable
arithmetic. If adopted, old tag-3 artifacts must continue to denote binary64 and
execute their old numeric behavior. New exact integers need explicit encoding;
no AST/source replay may reinterpret an old object as exact arithmetic.

If the product commits to exact full-width integral metadata, the tagged Number
option is preferable to silently rounded sizes or a public Int8/Int16/UInt32
family. Mixing integer and binary64 values must avoid converting the integer to
double before equality/order comparison, which would recreate the precision
bug. Do not assume it is free: larger tagged values, allocation, AOT code size
and byte-Range throughput require measurement.

## Three relations, not one overloaded promise

The current implementation already separates representation identity from
source equality. Make that distinction public in the algebra design:

1. Numerical equality, with a documented NaN policy.
2. A sorting/comparison relation, including a documented NaN position.
3. Bit identity for representations and compiler constants.

The present `CompareTo`-based relation is established source behavior, not IEEE
`==`/`<`: NaN equals itself and is below non-NaN. Preserve that behavior unless
a language-semantic transition is explicitly chosen. Add named IEEE unordered
predicates / total comparison / same-bits operations if users need both; do not
silently change `ranges.sort`, `contains`, `unique` and source comparison in
different directions. This is analogous to the explicit equality relations in
the [ECMAScript numeric specification][ecma], not a proposal to adopt JavaScript
coercion or BigInt/Number mixing rules.

## Algebra connection

On the current Native executable, with
`a = cast("1e16",Number)` and `b = - a`:

```text
(a + b) + 1 = 1
a + (b + 1) = 0
```

So `Number` plus floating addition is not an associative operation on its full
carrier. Excluding NaN and infinity does not restore associativity. `Finite`
also is not closed under addition/multiplication because overflow can create
infinity. Safe integral domains have partial operations at their boundaries.

An algebra Scope should name its carrier/domain, operations and equivalence
relation. Signature satisfaction is compiler-checkable; general associativity
and distributivity are not inferred from a `Function` tag or verified by a few
test samples. User-provided law claims must not authorize the optimizer to
reassociate floating expressions or parallelize a left fold. An exact additive
integer monoid, String concatenation and a floating sequential fold can all be
useful, but they make different promises. The [Haskell numeric class hierarchy]
[haskell] is a useful comparison for separating capabilities, not evidence that
calling a capability `Num` proves field laws.

## Suggested implementation sequence

1. Complete the specification and focused source/formatter/editor diagnostics
   for Number text and operators, with existing path scripts as compatibility
   discriminators. Retain the binary64 artifact tag and arithmetic order.
2. Unify discrete argument checks and iterator widths; add introspection / named
   comparison/representation operations, not blanket truthiness or quotas.
3. Express the closed numeric/domain capabilities through algebra Scope
   dictionaries; preserve sequential fold semantics unless the chosen algebra
   truly supplies the stronger law.
4. Decide exact integral representation from explicit metadata/arithmetic
   requirements, then prototype it with old-artifact and mixed-comparison
   migration probes. Do not mix this representation change into the surface fix.

Useful discriminators include source/O0/O2/O3 plus binary round trips for
fractional/exponent/signed-zero literals; endpoint-qualified numeric filenames;
2^53-neighbor literals and casts; NaN relations and hashing; zero-divisor and
remainder meanings; early Range demand and progress; and source/algebra
expressions whose mathematical but not floating reassociation changes a result.

## Primary sources

* [.NET Double][double]: binary64 precision and exceptional values; the host
  offers capabilities but that does not decide msync's source contract.
* [.NET Double.CompareTo][compare]: explains the current NaN-equal totalized
  ordering used by the interpreter.
* [ECMAScript Number and safe integer contracts][ecma]: primary example of
  documenting binary64 identity limits and multiple equality relations.
* [.NET numerics][numerics]: available exact integer implementation mechanism;
  adopting it does not require opening the language's source type universe.
* [Haskell numeric hierarchy][haskell]: capability separation and numeric
  operation families; a dictionary/trait name alone is not an algebra proof.

[double]: https://learn.microsoft.com/en-us/dotnet/api/system.double?view=net-10.0
[compare]: https://learn.microsoft.com/en-us/dotnet/api/system.double.compareto?view=net-10.0
[ecma]: https://262.ecma-international.org/15.0/#sec-number.max_safe_integer
[numerics]: https://learn.microsoft.com/en-us/dotnet/standard/numerics/
[haskell]: https://www.haskell.org/onlinereport/basic.html#numbers
