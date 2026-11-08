# Exhaustive matching

`match` is an expression. Every possible input must be covered. Bool can use
true/false arms; nullable values need a Null case; `_` catches remaining cases.
Each arm returns its block's final value.

```msync
let file = match exists ~/.codex {
    true => { ~/.codex/config.toml },
    false => { ~ },
};
let candidate? = if exists file { file } else { null };
let _ = match candidate {
    null => { false },
    _ => { candidate -> ./backup },
};
```

Regex patterns match String/Path values. A Path is projected to its native
string for matching; other types fall through rather than being stringified.
A regex cannot exhaust all possible text, so include a type or catch-all arm.

```msync
let matched = match ~/.codex/config.toml {
    <.+[.]toml> => { true },
    _ => { false },
};
let category = match decltype(file) {
    Path => { "filesystem" },
    _ => { "other" },
};
```

Regex bodies use the host regular-expression engine; source `<...>` delimiters
are distinct from operators. Named groups and escaped closing angles retain
their regex meaning.
