# Full Unicode string casing in the Native AOT runtime

## Preserved language contract

The Rust standard library implements `string.to_lowercase` and
`string.to_uppercase` through full Unicode default case conversion, not simple
one-code-point mapping. The installed Rust 1.99.1 baseline uses Unicode 17.0.0.
Replacing these calls with .NET `ToLowerInvariant`/`ToUpperInvariant` changes
ordinary results even with ICU enabled:

| Input and operation | Preserved result | .NET simple result |
| --- | --- | --- |
| `Straße`, uppercase | `STRASSE` | `STRAßE` |
| `İ`, lowercase | `i` plus U+0307 | `İ` |
| `ı`, uppercase | `I` | `ı` |
| `ΑΣ`, lowercase | `ας` (final sigma) | `ασ` |
| `ﬃ`, uppercase | `FFI` | `ﬃ` |

`InvariantGlobalization=true` does not cause this difference: the two .NET
deployment modes agreed in the isolated comparison. The language must preserve
full casing independently of whether the host installs ICU or a specific locale.
There is no exposed locale argument and no implicit Turkish or Lithuanian
tailoring. Casing does not normalize, trim or reinterpret string/path bytes.

## Implementation and normal path

`UnicodeCasing.ToUpper` and `ToLower` share pinned generated data from
[`vendor/unicode`](../../vendor/unicode/README.md). UnicodeData supplies simple
maps, unconditional SpecialCasing entries override them with full maps, and
DerivedCoreProperties supplies Cased and Case_Ignorable intervals.
The sole locale-independent conditional rule in Unicode 17 is Final_Sigma.
Its context uses the original string, with possessive skipping of Case_Ignorable
characters even when a character is also Cased (for example U+0345).

The relevant normative rules are
[Unicode 17 section 3.13](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/#G33992)
and the [SpecialCasing file format](https://www.unicode.org/Public/17.0.0/ucd/SpecialCasing.txt).
The [Rust string implementation](https://doc.rust-lang.org/src/alloc/str.rs.html)
is the retained behavior baseline. No .NET reflection, dynamic code generation,
runtime UCD parser, Python process, or native ICU library is used.

The dominant ASCII path first uses BCL's vectorized `Ascii.IsValid` and delegates
to its existing casing implementation. It does not construct tables, parse data
or perform per-character property searches. Non-ASCII conversion uses static
read-only spans and constant UTF-16 mapping text with binary search; no dictionary
initialization is charged to unrelated CLI or file-transfer operations. Context
scans are linear in the input; combining sequences between sigmas are not
rescanned for each preceding character.

`scripts/generate_unicode.py` is a fully typed offline generator. Generated
tables contain 1,488 lowercase and 1,580 uppercase nonidentity mappings, plus
158 merged Cased and 464 merged Case_Ignorable intervals. Product code consumes
the generated C# directly. Unicode License V3 attribution accompanies distribution.

## Focused verification

```text
python3 scripts/generate_unicode.py --check
python3 tests/Unicode/compare_rust.py
python3 tests/Unicode/compare_rust.py --native
```

The independent Rust oracle calls the same `String` operations as the old native
registry. The managed comparison examined every **1,112,064 Unicode scalar**:
all **3,037 nonidentity cases** matched, with mapping output SHA-256
`836db61b31c5bc2ad2b41db5915fb98653a9fe0703551744f1b42d1b3ba7d167`.
Another **2,018 deterministic string contexts** matched exactly, including final
sigma before/after combining marks, U+0345, punctuation and ZWJ, dotted/dotless I,
ligature expansions, Armenian, astral Deseret and uncased text.
The Native AOT comparison passed the same complete scalar and contextual sets
with the identical mapping digest under invariant globalization. Both isolated
builds passed with zero warnings/errors; generator and comparison scripts passed
Ruff and strict mypy.

Retained results are `.temp/unicode-v080/build/managed-results.json`,
`native-results.json` and adjacent mapping/context output. The actual language/CLI
suite separately passed exact UTF-8 casing, Unicode regex and native filenames
through source, `.mso` and `.mexe` in
`.temp/acceptance-v080/unicode-full-casing-corrected.json`; the focused scalar
oracle does not replace that end-to-end journey. The pending final package audit
uses the same language case against the installed Native AOT payload.
