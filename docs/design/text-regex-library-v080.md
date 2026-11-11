# Text construction and regular-expression records

## Ownership and source composition

`$std::text::StringBuilder` is a named Scope of shared associated functions.
`StringBuilder::new()` returns a fresh data Scope containing `chunks`, `length`
and a shared chunk policy. Aliasing an instance aliases that data; constructing
another instance does not. Operations receive the instance explicitly:

```msync
let Builder = $std::text::StringBuilder;
let text = Builder::new();
Builder::append(text, "hello");
let before = Builder::snapshot(text);
Builder::append_line(text, " Klee");
Builder::build(text) -> ./message.txt;
```

`append`, `append_line`, `append_all` and `clear` return Null. `build` and
`snapshot` return immutable String values, `len` counts Unicode scalars, and
`is_empty` tests that count. `append_line` appends LF independently of host
platform. Text input is explicit String, not implicit object stringification.
`with_policy` checks the supplied Scope against `$std::concepts::TextChunks`;
instances retain the selected policy rather than their own accessor closures.

The object implementation is executable source, not an opaque StringBuilder
value. One native `append_chunk(Range<String>, String)` primitive returns an
immutable persistent chain carried by the ordinary Range value. Appending to
that representation allocates one node and Range wrapper, without traversal or
copying of prior text. An ordinary input Range is consumed once to construct
the initial chain. Enumeration walks the chain iteratively into chronological
order; it does not recursively consume nested concat adapters. `build` uses
the existing native join primitive once. Previous chunk Range snapshots remain
valid, and no native global registry or new runtime type is needed.
The initial empty String gives empty/nonempty builders the same inferred schema.
The primitive's EagerRange contract evaluates inputs before publishing the
new mutable source binding; it cannot become a lazy self-reference to that slot.

The approach borrows append/finalize vocabulary from
[Java StringBuilder](https://docs.oracle.com/en/java/javase/26/docs/api/java.base/java/lang/StringBuilder.html),
but preserves msync's closed runtime types and source-level object mechanism.
It differs from repeatedly concatenating a growing String, which copies earlier
characters on every append. `build` remains linear in produced text and chunk
count, and produces a new snapshot each time. A first, purely source-composed
binary forest avoided copying but paid excessive interpreter overhead: a
prepared Debug VM run of 4096 32-character appends took 1174.850 ms and allocated
406,358,792 bytes. This measured cost justified one precise host primitive,
rather than introducing an opaque builder runtime kind or many native methods.

### Historical prepared interpreter measurement

The measurements below describe the earlier source-factory implementation and
its original interpreter revision, not the current shared-family interface or
the later allocation-optimized VM. They retain the causal evidence for choosing
the persistent-chunk primitive.

The final comparison uses the same snapshot-3 managed Debug assemblies,
optimization O2, 32-character chunks, one warm run, and the median of three
execution-only samples. Compilation is excluded; allocated bytes use
`GC.GetTotalAllocatedBytes(true)`. These are source-object/interpreter costs,
not claims about Native AOT startup, IO, or application-wide throughput.

| Appends | Source binary forest ms / bytes | Native persistent chunks ms / bytes |
| ---: | ---: | ---: |
| 64 | 31.475 / 10,957,816 | 14.816 / 4,544,808 |
| 512 | 115.915 / 52,561,832 | 26.050 / 11,434,824 |
| 4096 | 826.307 / 385,533,304 | 153.825 / 66,423,352 |

At 4096 appends, this is about 5.37 times faster and 82.8% fewer managed
allocated bytes in the measured execution. Remaining allocations include
ordinary Fn/Scope interpreter invocation and scheduling machinery; the primitive
does not remove that broader runtime cost.

The original `tests/StdlibTextRegex --bench` driver accepted `--bench-source=PATH`
to select a source baseline without replacing product files. The forest baseline is
`a426bb6:stdlib/text.msync`, with each `merge` and `tail` result resolved using
`ranges.empty` before overwriting the captured operand binding. That resolution
is required to prevent deferred Range self-reference in the old source design.

The earlier snapshot-4 embedded module passed 207 focused contracts across
O0/O2/O3, source execution, `.mso` round-trip, and bound `.mexe` round-trip.
These include eager generic-input consumption, prior Range/String snapshots,
factory independence and aliases, clear/reuse, chunk ordering, optional and
lookbehind groups, scalar offsets, fresh replayed record identity, and selected
pattern diagnostics. The existing Unicode/Path module journey also passes 412
contracts after migration to `as` and `Path @ Connection` syntax.

`join_lines`, `indent`, and `unindent` compose the existing scalar String and
Range operations. The latter two normalize line boundaries to LF and do not
retain a final input line terminator, following `string.lines`.

## Invariant brace formatting (0.8.6)

Use `$std::text::format(template, ...values)` for positional arguments or
`$std::text::format_fields(template, scope)` for named fields:

```msync
let text = $std::text;
let total = text::format("{}: {:.2f}", "total", 12.5);
let row = text::format_fields("{name:<10} {count:04d}",
    scope { name: "files", count: 12 });
```

Templates are ordinary Strings, including dynamically supplied Strings.
Positional fields use either automatic `{}` or zero-based `{0}` indexes; the
two modes cannot mix. Named fields select exact members of the supplied Scope,
not lexical caller names. `{{` and `}}` write literal braces. Additional
arguments are evaluated normally and need not all be referenced.

After a colon, a field admits `[fill/alignment][sign][#][0][width][.precision][presentation]`.
Alignment is `<`, `>`, or `^`, optionally preceded by one Unicode scalar fill.
Text defaults left, Number right. Width is a minimum; width and text truncation
count Unicode scalars, not UTF-16 units, grapheme clusters, or terminal columns.
For example, `{:🙂^5}` centers text in a five-scalar field and `{:.3s}` takes at
most three scalars. Debug `{:?}` quotes String/Path text with backslash escapes;
it is readable diagnostic text, not a source literal or serialized artifact.

Number formats include `f/F` (fixed), `e/E` (scientific), `g/G` (general), and
`d`, `x/X`, `b/B`, `o` (integer representations). Floating output uses .NET
invariant binary64 rounding and exponent spelling. Default Number output
preserves signed zero; precision without an explicit floating presentation is
general/significant-digit precision. Integer formats require a finite integral
Number and describe the actual represented magnitude, including values beyond
Int64; they do not truncate fractions or introduce another public number type.
`#` adds the language's base prefix to binary, octal, or hex. `+` and space
select a positive sign, and `0` pads after sign/prefix. Use an explicit fill
before alignment, such as `0>`, instead of combining alignment and the zero flag.

The surface takes brace fields from [Rust `std::fmt`](https://doc.rust-lang.org/std/fmt/)
and [WG21 P0645](https://www.open-std.org/jtc1/sc22/wg21/docs/papers/2019/p0645r10.html),
strict incompatible-flag diagnostics from
[Java Formatter](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/util/Formatter.html),
and invariant numeric conversion from
[.NET standard formatting](https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings).
This is one small format language, not a mix of percent, composite-format, and
interpolation grammars. One Core reader serves literal checking and execution.
Literal calls diagnose malformed fields, absent arguments/members and known
type mismatches at the template expression; dynamic calls report the same
field error at execution, with its one-based scalar position in the template.

The formatter returns a String and owns no output stream. A Range, Scope,
Function or Process has a compact display; the formatter does not enumerate
ranges, traverse object graphs, or wait for a Process to exit. Named formatting
reads only selected members and resolves a selected deferred value normally.
Unselected deferred fields do not delay formatting. The named native has an
explicit local-memory read effect, so a change to a Scope field cannot be
hidden by pure-call reuse. Positional formatting checks the actual value
provided by ordinary argument evaluation. Bytecode validates the fixed prefix
of a variadic native call, matching source checking instead of repeating the
template parameter's String type over the argument pack.

The actual CLI journey in `tests/Msync.EndToEnd/stdlib_format_journey.py` checks
byte-exact results at O0/O2, source-free HIR, relocatable and executable stages.
A child waits on a file written by formatting, proving that formatting its
Process, a lazy Range, and a Scope with an unselected pending field does not
block the publication; selecting the pending field then reads its output.

## Regular expressions

`$std::regex` exposes `is_match`, `find`, `matches`, `replace`, `split`, and
`escape`. Patterns and replacement syntax follow
[.NET regular expressions](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expressions).
Patterns are ordinary Strings, with inline options such as `(?im)`; there is no
new regex runtime kind. Culture-invariant matching avoids host locale changes.
The BCL static regex cache handles reused patterns; Native AOT does not emit new
machine code. `Pattern::new(expression)` returns a data Scope containing
`expression`. Shared associated operations such as
`Pattern::matches(pattern, text)` and `Pattern::replace(pattern, text, replacement)`
read that field directly; there is no parallel captured pattern or getter that
can disagree with it. Construction performs no matching, eager validation or
JIT compilation. `$std::concepts::PatternSpec` constrains the record consumed by
the associated functions.

| Result | Fields |
| --- | --- |
| Match | `value: String`, `index: Number`, `length: Number`, `groups: Range<Scope>` |
| Group | `name: String`, `value: String`, `index: Number`, `length: Number`, `success: Bool` |

`find` returns Null for no match; callers must narrow its nullable binding before
member access. `matches` returns records in engine order. Groups include the
whole-match group `0`, unnamed numbered groups, and named groups, in the engine's
order. An unsuccessful optional group has `success: false`, `index: -1`, empty
value and length zero. Repeated groups expose the final captured Group value,
as in the BCL Group API, not a fabricated history of captures.

Indexes and lengths are converted to Unicode scalar counts, matching msync
String indexing. The regex engine itself retains .NET's UTF-16 pattern semantics;
for example a dot's matching unit is not redefined as a grapheme cluster. Match
prefixes are counted incrementally, rather than scanning the whole prefix once
per match. Group positions within a match reuse that prefix count; lookbehind
captures before the match use their actual earlier position.

`matches` parses the pattern when its deferred Range call is first resolved,
and performs matching as that Range is consumed. An unused Range does not
validate its pattern. Enumeration replays engine matches and creates fresh mutable record
Scopes, so modifying a returned record does not mutate the input String or the
next enumeration's records. Invalid selected patterns preserve the BCL reason
under the source operation name. Unselected source branches do not parse or
match. No arbitrary input quota or timeout is added by this library.

`values`, `count`, and `remove` are ordinary source compositions over the native
primitives. Native signatures carry MayFail for pattern-taking operations;
`escape` is pure and cannot fail on a String input. Compiler record inference
exposes all Match and Group fields to function specialization and editor tools.
