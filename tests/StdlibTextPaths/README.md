# Unicode text and lexical Path library journeys

Run `dotnet run --project tests/StdlibTextPaths` from the repository root.
`--native-only` isolates the new host primitives while shared compiler changes
are being integrated. The complete entry compiles actual standard-library
source at O0/O2/O3, executes it, and independently round-trips relocatable and
context-bound binary artifacts.

String positions and lengths count Unicode scalar values, not UTF-16 code units
or grapheme clusters. `find`/`rfind` return nullable Number, avoiding a fabricated
unsigned `npos` value. `substring` admits a position at the end and clamps count;
`char_at` requires an actual element. Counts/indices are finite nonnegative
integral Number values; clamping and empty repetition do not impose an arbitrary
count quota. `lines` follows CR/LF/CRLF line-reader behavior, with no extra record
after a final terminator; `split` retains its pre-existing separator semantics.

The source helpers exercise nullable positions, partition records, casing,
prefix/suffix editing and padding. `join` consumes its Range exactly once with a
StringBuilder; its native behavior records enumeration and selected errors so
discarding the returned String does not discard callbacks or their effects.

Path wrappers reuse existing lexical filesystem primitives. SSH examples never
connect: they verify that symbolic spelling, endpoint ownership and literal
POSIX backslashes survive composition and object/executable serialization.

The implementation follows the [C++ string operation categories](https://eel.is/c%2B%2Bdraft/string.view.ops)
and [filesystem path observers](https://eel.is/c%2B%2Bdraft/filesystems), but uses
the language's scalar-text model rather than copying C++ code-unit indices.
The [Rune API](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction)
defines the scalar versus grapheme distinction.
