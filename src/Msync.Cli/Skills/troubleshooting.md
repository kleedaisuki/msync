# Diagnose and repair a program

Read a diagnostic as a source explanation: error code and cause, primary
expression, related call site, then help or a suggested edit. In VS Code,
hover shows inferred types and function signatures; a quick fix is offered
when a specific insertion or replacement is known.

## A Path was used as a Scope or Connection

An unbound bare name is a Path. For `value::member`, pass the lexical Scope
instance that owns `member`; for `path @ endpoint`, use a Connection on the
right and a Path or String on the left.
A constructor's `::x` member may already be a Path, not the original instance.
Read `msync skill language scopes` for the constructor/accessor example.

## A possibly Null value reaches an operation

Admit Null with `let file? = ...`, then use `if file is not null { ... }` or
an exhaustive `match`. Postfix `file?` tests **is null**. Do not treat it as a
truthiness test or write `file != null`.

## A function returned Null unexpectedly

A final `;` discards the preceding expression. Remove it from a block tail when
that value is the intended return. Declarations and empty blocks yield Null.

## A file transfer wrote a filename as text

A String source writes literal content. A Path source reads the file. Use a
Path expression such as `./input.txt`, or an explicit Path annotation/parameter
when converting a String is intentional.

## A declaration does not parse

Terminate declarations with `;`, and separate Scope members or match arms with
commas. Braces, not indentation, delimit blocks. Strings double embedded quotes
and preserve literal backslashes; do not assume C-style escapes.

## An operation failed after some files completed

Read the actual native cause and endpoint/path. Completed cohort members can
remain; correct the cause and retransmit. msync does not impose persistent
transactional rollback on normal copies. `msync doctor` inspects optional native
transport availability, and `msync config` shows settings and origins.
