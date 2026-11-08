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
- `path`: path joining, endpoint qualification and identity.
- `string`: Unicode casing, searching, replacement, splitting and trimming.
- `bool`: Bool composition as ordinary source functions.
- `ranges`: lazy range views, filtering, transformation and typed callbacks.
- `process`: inspect completed native Process values.

Range callbacks are `fn` values. Lazy ranges evaluate only demanded elements;
constructing a range does not imply executing every effectful item.

```msync
let selected = $std::ranges::filter([true, false], fn(value) { value });
let first = $std::ranges::take(selected, 1);
```

The editor can navigate to these same embedded sources through versioned
`msync-stdlib` documents. For command options rather than language APIs, run
`msync --help`.
