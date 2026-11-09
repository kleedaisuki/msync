# Control-header path boundaries

Run the focused contract with an isolated build directory:

```sh
dotnet run --project tests/ControlHeaderPaths -c Release \
  --artifacts-path .temp/control-header-path-build
```

If, match and for own the block following an unparenthesized header. A trailing
path separator therefore ends before that block, including a bare `/` root.
The parser propagates this boundary only along that header's expression spine.
Parentheses, call arguments, Range elements and block contents establish their
own ordinary expression context; computed block-valued path operands remain
legal there. Return and break retain their block-valued operands. A separated
operator also ends a trailing path (`exists ./ and exists ./`), whereas an
adjacent keyword remains filename data (`./and`, `./or`, `./is`, `./as`). The
formatter uses the actual cast operator span rather than joining `./ as Type`
into a different filename.

The program checks those AST boundaries, editor parsing and formatter
idempotence, then runs filesystem-dependent examples and their formatted
equivalents at O0/O2/O3 through source, relocatable bytecode and source-free
contextual executable bytecode. Windows path spellings are syntax checks on
every host, not assumptions about the local filesystem.
