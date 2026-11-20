# CLI routing and command-contract audit (0.9.2)

The interactive terminal and the language server account for much of the CLI
project's source; their size is separate from command-line routing complexity.
The latter is visible in three owners:

- [Program](../../src/Msync.Cli/Program.cs) handles helper dispatch and scans raw
  arguments for language-server, help and version tokens before parsing.
- [CommandOptions](../../src/Msync.Cli/Commands/CommandOptions.cs) parses shared
  options, resolves implicit commands and output-suffix inference, and carries
  options for unrelated operations in one record.
- [NativeCommandPipeline](../../src/Msync.Cli/Commands/NativeCommandPipeline.cs)
  branches on the command again, after Program has already decided whether to
  read or lower the input. Even source `check` currently lowers to Native and
  optimizes before printing success.

A frozen 0.9.2 Native AOT CLI running from a disposable directory with
`sample.msync` containing `42;` showed:

| Invocation | Actual behavior |
| --- | --- |
| `run --config --version` | Prints version, exit 0: the raw scan intercepts the token before the parser can consume the option operand. |
| `check --dry-run sample.msync` | Prints valid, exit 0: a runtime option is accepted but unused for check. |
| `run -- sample.msync` | Rejects the conventional end-of-options marker, exit 1. |
| `check --no-config --telemetry-db check.sqlite --telemetry-level phases sample.msync` | Prints valid and records compile, lower and optimize spans, not execution or contextual preparation. |

## Proposed direction

Normalize existing spellings once at ingress into a typed request for a
concrete operation. Model the request's input, output, permitted options and
required lifecycle explicitly; downstream stage functions should not branch on
the original command spelling. Keep help and editor/automation contracts as
separate lifecycle concerns, but do not let them reinterpret raw argv. Treat
existing valid aliases, artifact formats, source inference, stdout protocols
and exit/cancellation codes as compatibility contracts. Capture actual VS Code,
help/config, pipe/stdin, REPL and source/artifact journeys before changing
dispatch. Do not shorten the `check` pipeline until failed native-stage
diagnostics are classified and the validation contract is verified.

[Git's option parser](https://git-scm.com/docs/api-parse-options.html) provides
precedent for defining subcommand ownership, option arity and an explicit end
of options. This does not require adopting another parser dependency. The first
change should eliminate duplicate routing without renaming commands; surface
pruning and per-command work reduction are separate decisions.

## Portable compilation versus contextual preparation

Two artifact routes are useful; exposing their internal names as a mandatory
command sequence is not. Both `.mso` and `.mexe` already run without source:
the former prepares against the current context at run time, the latter
carries context and any pre-expanded work. Neither is an OS executable.

A Native AOT smoke test compiled
`./inputs/<.+\.pick> -> ./frozen/;` first to a portable object and bound the
same source directly (no intermediate object required). Binding wrote the
artifact but did not create the transfer target. After adding a matching
file, `run` of the bound artifact copied only the original match; `run` of
the portable artifact copied both. In the same test
`compile frozen.mexe -o surprise.mso` succeeded and disassembly still reported
`stage: Executable`. The output's suffix does not make a bound program
portable; the command is hiding a pass-through of an already lowered program.

The product intent is artifact export and moving compilation cost out of
the run path, not making users pick a compiler phase or inventing a separate
workflow for saved plans. The two formats support that one intent at different
boundaries: a portable program pays compilation up front; a prepared program
can also pay contextual preparation up front, at the cost of retaining
context and any already resolved selections.

The implemented public framing is one `run FILE` for source or artifacts, and
one `compile FILE` to save a program. The default saves a portable
`.mso` next to the input; an explicit `--prepare` requests the contextual
`.mexe` form directly, with no intermediate file to manage. When the output
is inferred, report its path and form; explicit `-o` remains quiet, including
byte-only `-o -`. Stdin source requires an explicit output. Known artifact
suffixes must agree with the selected mode. An existing prepared input cannot
be re-exported as if it were portable or freshly prepared. HIR export uses
`--emit hir`, and human inspection uses `disassemble`.

Legacy spellings still parse, but no longer teach the ordinary workflow in the
main help and usage examples. Artifact formats have not changed. A prepared
program can retain unresolved work and is not a filesystem snapshot or a
validity guarantee. The published Linux Native AOT build passed the
[CLI export journey](../../tests/Msync.EndToEnd/journeys.py), including
source-free execution, frozen versus live selection, both binary stdout
forms, output errors and telemetry ownership; the contextual artifact and
editor inspection journeys also passed. The earlier routing observations
are not addressed by this export change.
