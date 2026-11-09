# Text construction and regular-expression records

## Ownership and source composition

`$std::text::Builder()` returns a fresh ordinary Scope whose methods close over
shared mutable storage. Aliasing a builder aliases that storage; calling the
factory again does not. `append`, `append_line`, `append_all`, and `clear` return
Null. `build` returns an immutable String snapshot, `length` counts Unicode
scalars, and `empty` tests that count. `append_line` appends LF independently of
host platform. Text input is explicit String, not implicit object stringification.

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

`join_lines`, `indent`, and `unindent` compose the existing scalar String and
Range operations. The latter two normalize line boundaries to LF and do not
retain a final input line terminator, following `string.lines`.

## Regular expressions

`$std::regex` exposes `is_match`, `find`, `matches`, `replace`, `split`, and
`escape`. Patterns and replacement syntax follow
[.NET regular expressions](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expressions).
Patterns are ordinary Strings, with inline options such as `(?im)`; there is no
new regex runtime kind. Culture-invariant matching avoids host locale changes.
The BCL static regex cache handles reused patterns; Native AOT does not emit new
machine code. `compile(pattern)` binds a pattern in a fresh Scope of methods;
it is a source-level convenience, not eager validation or a JIT promise.
`pattern()` is a getter: no mutable copied field can silently disagree with the
operations' captured pattern.

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
