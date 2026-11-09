# Source-backed standard library

`$std` is a Scope. Each module is another Scope with ordinary source functions
and explicit host-native declarations. Module topic pages show the executable
embedded declarations, not a separately maintained list of signatures.

```msync
let fs = $std::fs;
let config = ~/.codex/config.toml;
let available = fs::exists(config);
let upper = $std::string::to_uppercase("hello");
let combined = $std::bool::both(available, true);
```

- `fs`: metadata, filesystem queries, iteration and explicit mutations.
- `cast`: explicit conversion with a Type value as its target.
- `functional`: function and predicate composition using ordinary closures.
- `io`: UTF-8 text, line and byte Ranges, replacement and append operations.
- `math`: Number arithmetic, floating-point classification and source utilities.
- `path`: lexical decomposition, composition and endpoint qualification.
- `string`: Unicode scalar access, searching, slices, joins and text tools.
- `bool`: Bool composition as ordinary source functions.
- `ranges`: lazy adapters, structured elements, searches, folds and sorting.
- `process`: Process inspection, explicit waiting and live process control.

The two layers serve different purposes: a small primitive layer supplies host
and iteration capabilities, while source functions provide a broad expression
vocabulary. There is no separate Array namespace: use Range for sequence work.

Range callbacks are `fn` values. Lazy ranges evaluate only demanded elements;
constructing a range does not imply executing every effectful item.

```msync
let selected = $std::ranges::filter([true, false], fn(value) { value });
let first = $std::ranges::take(selected, 1);
```

Adapters are lazy; terminals consume their source. Buffered operations such as
sorting need finite input. Materialization preserves object references rather
than cloning Scope instances. String positions count Unicode scalar values,
not UTF-16 code units or displayed grapheme clusters.

```msync
let number = $std::cast("42", Number);
let name = $std::cast("./report.txt", Path);
let normalize = $std::functional::compose(
    $std::string::to_uppercase, $std::string::trim
);
let labels = $std::ranges::transform([" first ", " second "], normalize);
let joined = $std::string::join(labels, ", ");
```

An explicit cast changes the actual value, not only its inferred type. This
matters for transfers: String writes text, whereas Path copies file content.
Null still requires an admitted nullable type; it does not gain truthiness.

The editor can navigate to these same embedded sources through versioned
`msync-stdlib` documents. For command options rather than language APIs, run
`msync --help`.
