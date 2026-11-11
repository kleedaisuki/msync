# Format values and compose console pipelines

`$std::text::format` returns a String. It does not print, start a Process, or
enumerate a Range just to display it. Use `{}` for the next argument, `{0}` for
an explicit position, and `{{` or `}}` for literal braces. A specifier such as
`{:.2f}` selects a fixed-point Number with two fractional digits.

`$std::io::print` and `println` write raw text to standard output; `eprint` and
`eprintln` select standard error. Plain output does not interpret braces. A
`println` adds the host line terminator. Use `flush` when another process needs
to see a prompt before you wait for input.

`read_line()` returns a nullable String: `null` means EOF and `""` means a
real empty line. `stdin_lines()` is a lazy one-pass view of the same standard
input, not a replayable snapshot. Keep `for` for streaming input; use a `Cursor`
when you need explicit positions in a finite snapshot.

Save this program and pipe it newline-separated numbers. It ignores blank
lines with a message on standard error, deduplicates and orders the numbers,
then writes fixed-precision output without adding a dummy set member.

```msync
let io = $std::io;
let text = $std::text;
let Set = $std::containers::Set;
let Cursor = $std::ranges::Cursor;
let numbers = Set::new(Number);
for line in io::stdin_lines() {
    if line == "" {
        io::eprintln("ignored blank line");
    } else {
        Set::insert(numbers, line as Number);
    };
};
let cursor = Set::cursor(numbers);
loop {
    if not Cursor::has_value(cursor) { break; };
    io::println(text::format("{:.2f}", Cursor::value(cursor)));
    Cursor::next(cursor);
};
```

The same program can be compiled before its input is available. Compilation,
artifact inspection and context binding do not consume standard input.
See `msync skill library text format` and `msync skill library io read_line`
for the embedded declarations.
