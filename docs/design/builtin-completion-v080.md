# Built-in operand completion in v0.8

`$` is an operator, not part of the identifier inserted by completion. Both the
language server and interactive prompt use `BuiltinCompletion` before ordinary
lexical or filesystem candidates. The root catalog is taken from the compiler's
actual built-in bindings; it is not a second list of accepted source names.
The public roots are `$std`, `$local`, `$wsl`, `$pwd`, `$file`, `$env` and `$path`.
Range operations belong to `$std::ranges`; removed range-view aliases are not
advertised as public source operands.

For `$en<caret>`, the completion edit replaces `en` with `env` and leaves the
already typed `$` unchanged. A cursor in the middle of an identifier replaces
the remainder of that identifier too. The edit is suppressed inside ordinary
strings, comments and regex literals. The protocol response includes an explicit
range, so VS Code's default word range cannot leave one `$` while inserting a
second one from the label.

| Operand | Candidate source and meaning |
| --- | --- |
| `$std::` | The actual typed embedded standard library, including `$std::ranges`; the ordinary semantic member provider owns these declarations |
| `$env::` | Names in `Environment.GetEnvironmentVariables()` for this process, with native Windows versus Unix name comparison; selected values have the runtime's Path semantics |
| `$path::` | Existing native PATH executables; Windows PATHEXT and Unix executable permission bits determine candidates |
| `$pwd::` | The `pwd` value plus physical entries relative to the current working directory |
| `$file::` | `this`, `parent`, and entries relative to the actual named source file's directory; an unnamed REPL source must not guess the working directory |
| `$local::` | The ordinary physical path provider with the captured local context |
| `$wsl::` | The existing selected native WSL directory session, including its foreground authentication/cancellation ownership |

Typed aliases use the receiver's inferred Connection identity rather than its
spelling: `let environment = $env; environment::...` queries the same environment
namespace. The standard library remains a lexical Scope, while physical and
logical Connection kinds dispatch to their own capabilities. An environment or
PATH connection is never treated as an arbitrary local directory.

Environment completion reads names, not values, because names are the useful
menu candidates. The environment is the language-server or REPL process's
inherited environment; changing a different running shell does not mutate that
process. PATH enumeration follows configured directory order and deduplicates
names using the native platform comparison. Missing or inaccessible PATH
locations are skipped in the same manner as an executable search; ordinary
filesystem completion retains its own useful I/O diagnostics.

Root and logical-namespace descriptions are cheap and returned eagerly. They do
not carry lazy semantic-resolution data that might confuse `env` (the inserted
name) with `$env` (the full source operand). LSP and REPL apply the same source
spans and candidate text. Only explicit completion queries inspect environment
or PATH state; highlighting and gray hints do not perform those reads.
