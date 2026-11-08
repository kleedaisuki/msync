# Paths, literal text and transfers

`source -> destination` returns Bool. A Path source copies filesystem content;
a String source writes literal UTF-8 text. A final semicolon discards that Bool.

```msync
./input.txt -> ./backup.txt;
"hello" -> ./message.txt;
let copied = ~/.codex -> ./codex-backup;
```

Use `/` to compose path values. `~` uses the endpoint's home directory. Relative
paths retain their captured local context; changing the REPL directory does
not retroactively rebase an existing binding.

```msync
let root = ~/.codex;
let config = root / config.toml;
let files = $std::fs::directory_iterator(root);
```

Unbound bare names are Paths. Quoting them makes literal text instead. Names
with spaces can be bare filesystem spelling, but a String-to-Path annotation
or parameter makes intent explicit when source is ambiguous.

A finite Range transfer is one selected operation; an explicit `for` loop
performs separate iterations. Local ordinary files use owned same-directory
staging and atomic replacement. A failed cohort can leave completed members;
read the actual result and retransmit as needed rather than assuming rollback.

```msync
for file in ~/.codex/<.+> {
    file -> ./backup/
};
```

Use `msync skill paths connections` to select a remote endpoint. Native Path
separators follow that endpoint; a POSIX backslash is filename data, including
when the interpreter itself runs on Windows.
