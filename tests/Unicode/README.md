# Unicode 17 conformance

Run `python3 tests/Unicode/verify_unicode.py` to compare the C# conformance
executable with the pinned Unicode Character Database under `vendor/unicode`.
The expected full mappings and Final_Sigma contexts are derived from those
normative files rather than from Python's installed Unicode version. Ordinal
comparison uses code-point lexicographic ordering.

The focused check examines all valid Unicode scalars, 2,018 deterministic string
contexts and 10,008 ordering pairs. `--order-only` selects only ordering;
`--native` publishes the same C# conformance executable with Native AOT. Pass
`--binary PATH` to reuse an existing compatible conformance image instead of
rebuilding product projects during another workstream.

The former Rust oracle is retired with the Rust implementation. Its historical
comparison remains recorded in `docs/design/unicode-v080.md`; current validation
requires only .NET and the pinned Unicode data. Product execution never starts
this Python verifier or parses UCD files.

Disposable builds, outputs and reports remain in `.temp/unicode-v080`. The
ordinary source / source-free `.mso` / bound `.mexe` journey is
`full_unicode_case_regex_and_native_filename` in
`tests/Acceptance/language_journeys.py`.
