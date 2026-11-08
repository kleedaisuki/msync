# Full Unicode casing acceptance

Run `python3 tests/Unicode/compare_rust.py` to compare the generated C# runtime
with the retained Unicode 17 Rust `String` semantics. Add `--native` to compile
the focused oracle with Native AOT and invariant globalization before comparison.
The oracle checks all valid Unicode scalar inputs and 2,018 deterministic string
contexts; it does not repeat unrelated compiler, filesystem or transport tests.

Use `--order-only` to check only scalar ordinal comparison against Rust across
10,008 deterministic pairs, without repeating accepted full casing measurements.

The Rust oracle deliberately names its Unicode version: a future Unicode upgrade
must choose updated language behavior explicitly rather than silently inheriting
whatever casing tables a newer host runtime installs.

Disposable builds, map/context outputs and JSON results remain under
`.temp/unicode-v080`. The ordinary CLI source / source-free `.mso` / bound `.mexe`
journey is `full_unicode_case_regex_and_native_filename` in
`tests/Acceptance/language_journeys.py`.

See `docs/design/unicode-v080.md` for representation, property-context semantics,
ASCII fast-path cost and the actual Native AOT verification record.
