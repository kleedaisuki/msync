# Unicode Character Database 17.0.0

These are the unmodified Unicode 17.0.0 data files used to preserve the Rust
0.7.3 standard library's full, locale-independent string casing in C# 0.8.
The retained Rust implementation was built with Unicode version `(17, 0, 0)`.

Upstream sources:

* <https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt>
* <https://www.unicode.org/Public/17.0.0/ucd/SpecialCasing.txt>
* <https://www.unicode.org/Public/17.0.0/ucd/DerivedCoreProperties.txt>
* <https://www.unicode.org/license.txt>

`LICENSE.txt` contains the Unicode License V3 notice for distribution. The raw
data is development input; the product uses generated read-only tables in
`src/Msync.Runtime/UnicodeCasingData.cs`, not runtime parsing of these files.

Regenerate offline with `python3 scripts/generate_unicode.py`, or verify that
the generated source agrees with the pinned inputs using `--check`.
`UnicodeCasing.cs` implements the default context-sensitive Final_Sigma rule;
language-specific Turkish/Azeri/Lithuanian tailoring is not the default mapping.

SHA-256 of the retrieved upstream bytes:

```text
24c7fed1195c482faaefd5c1e7eb821c5ee1fb6de07ecdbaa64b56a99da22c08  17.0.0/DerivedCoreProperties.txt
efc25faf19de21b92c1194c111c932e03d2a5eaf18194e33f1156e96de4c9588  17.0.0/SpecialCasing.txt
2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c  17.0.0/UnicodeData.txt
e7a93b009565cfce55919a381437ac4db883e9da2126fa28b91d12732bc53d96  LICENSE.txt
```
