using Msync.Cli;
using Msync.Compiler;
using PrettyPrompt.Highlighting;

int assertions = 0;
void Color(string source, string token, AnsiColor? expected, IReadOnlyDictionary<string, SemanticMember>? bindings = null)
{
    int start = source.IndexOf(token, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException("Missing fixture token: " + token);
    var spans = PromptSyntax.Highlight(source, bindings);
    foreach (var span in spans)
    {
        if (span.Start >= 0 && span.End <= source.Length && span.Length > 0) continue;
        throw new InvalidOperationException("Highlight span lies outside the source.");
    }
    for (int offset = start; offset < start + token.Length; offset++)
    {
        var matches = spans.Where(span => span.Start <= offset && offset < span.End).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Highlight spans overlap.");
        AnsiColor? actual = matches.Length == 0 ? null : matches[0].Formatting.Foreground;
        assertions++;
        if (actual != expected)
            throw new InvalidOperationException($"Token '{token}' in '{source}': expected {expected?.ToString() ?? "terminal foreground"}, got {actual?.ToString() ?? "terminal foreground"}.");
    }
}

const string declaration = "fn copy(file: Path) -> Path { file }; scope Settings { root: ~/.codex };";
Color(declaration, "fn", AnsiColor.Cyan);
Color("global root, .codex;", "global", AnsiColor.Cyan);
Color(declaration, "copy", AnsiColor.Yellow);
Color(declaration, "scope", AnsiColor.Cyan);
Color(declaration, "Settings", AnsiColor.Magenta);
Color(declaration, "Path", AnsiColor.Magenta);
Color(declaration, "file", null);
Color(declaration, "{", null);
Color(declaration, "~/.codex", null);
Color("let .codex = ~/.codex; .codex", ".codex", null);
Color("let file-name = true; file-name", "file-name", null);
Color("let anonymous = scope { field: true };", "anonymous", null);
Color("if exists file { file -> destination } else { null }; file?", "if", AnsiColor.Magenta);
Color("if exists file { file -> destination } else { null }; file?", "?", AnsiColor.Magenta);
Color("file -> destination", "->", AnsiColor.Cyan);
Color("$env @ HOME", "$", AnsiColor.Cyan);
Color("$env @ HOME", "env", null);
Color("$env @ HOME", "HOME", null);
Color("ssh://forge @ ./file", "ssh://", AnsiColor.Cyan);
Color("ssh://forge @ ./file", "forge", null);
Color("ssh://user@forge", "@", null);
Color("let text = \"a doubled \"\"quote\"\"\";", "\"a doubled \"\"quote\"\"\"", AnsiColor.Green);
Color("# fn if scope", "fn if scope", AnsiColor.BrightBlack);
Color("let selected = match file { <(?<fn>if|scope)?> => { true } };", "<(?<fn>if|scope)?>", AnsiColor.Magenta);
Color("let count = 12;", "12", AnsiColor.Magenta);
foreach (string number in new[] { "1.25e-3", "0xff", "0b10", "0o7", "1_000", ".5", "0" })
    Color("let number = " + number + ";", number, AnsiColor.Magenta);
Color("2/3+4%2", "3", AnsiColor.Magenta);
Color("2**3**2", "**", AnsiColor.Cyan);
Color("-value", "-", AnsiColor.Cyan);
Color("copy-config", "copy-config", null);
Color("./-filename", "-filename", null);
Color("./123/0xff", "123", null);
Color("./123/0xff", "0xff", null);
Color(".5notes", ".5notes", null);
Color("value as Number", "as", AnsiColor.Magenta);
Color("$local @ ./file", "@", AnsiColor.Cyan);
Color("concept Numeric { value: Number };", "concept", AnsiColor.Cyan);
Color("concept Numeric { value: Number };", "Numeric", AnsiColor.Magenta);
const string inlineProduct = "concept Product { first?: typename First, second?: typename Second, };";
Color(inlineProduct, "typename", AnsiColor.Cyan);
Color(inlineProduct, "First", AnsiColor.Magenta);
Color(inlineProduct, "Second", AnsiColor.Magenta);
Color("concept Bag { items: Range<typename Item>, };", "typename", AnsiColor.Cyan);
Color("concept Bag { items: Range<typename Item>, };", "Item", AnsiColor.Magenta);
Color("concept Operation { call: fn(value: typename Item) -> Item, };", "typename", AnsiColor.Cyan);
Color("concept Operation { call: fn(value: typename Item) -> Item, };", "Item", AnsiColor.Magenta);
Color("concept Legacy { type Item; item: Item, };", "type", AnsiColor.Cyan);
Color("concept Legacy { type Item; item: Item, };", "Item", AnsiColor.Magenta);
if (!PromptSyntax.Keywords.Contains("typename", StringComparer.Ordinal)
    || PromptSyntax.Keywords.Contains("type", StringComparer.Ordinal))
    throw new InvalidOperationException("Completion should prefer inline typename declarations.");
if (PromptSyntax.Hint("concept", "concept".Length, new Dictionary<string, SemanticMember>()) is not { } conceptHint
    || !conceptHint.Text.Contains("typename", StringComparison.Ordinal)
    || conceptHint.Text.Contains("type Self;", StringComparison.Ordinal))
    throw new InvalidOperationException("Concept hint should show the canonical inline associated declaration.");
if (PromptSyntax.Hint("atlas", 5, new Dictionary<string, SemanticMember>()) is not null)
    throw new InvalidOperationException("A name suffix is not an 'as' keyword.");

Dictionary<string, SemanticMember> bindings = new(StringComparer.Ordinal)
{
    ["operation"] = new("operation", "Function", "fn operation(value) -> Bool", "", SemanticRole: "function"),
    ["Named"] = new("Named", "Scope", "Named: Scope", "", SemanticRole: "type"),
    ["instance"] = new("instance", "Scope", "instance: Scope", "", SemanticRole: "variable"),
    ["item"] = new("item", "Path", "item: Path", ""),
    [".codex"] = new(".codex", "Path", ".codex: Path", ""),
    ["Named::get-path"] = new("get-path", "Function", "fn get-path() -> Path", "", SemanticRole: "function")
};
Color("operation(item)", "operation", AnsiColor.Yellow, bindings);
Color("operation(item)", "item", null, bindings);
Color("Named::field", "Named", AnsiColor.Magenta, bindings);
Color("instance::field", "instance", null, bindings);
Color(".codex -> item", ".codex", null, bindings);
Color("Named::\"get-path\"()", "\"get-path\"", AnsiColor.Yellow, bindings);
Console.WriteLine($"{assertions} terminal palette character and span assertions passed.");

if (args.Contains("--measure", StringComparer.Ordinal))
{
    const int repetitions = 10000;
    const string line = "fn copy(file: Path) -> Path { file }; let number = 0xff + 1e-3; number as String";
    int count = 0;
    for (int index = 0; index < 1000; index++) count += PromptSyntax.Highlight(line, bindings).Count;
    long allocation = GC.GetAllocatedBytesForCurrentThread();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (int index = 0; index < repetitions; index++) count += PromptSyntax.Highlight(line, bindings).Count;
    clock.Stop();
    allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
    Console.WriteLine($"Prompt Highlight: {clock.Elapsed.TotalMilliseconds / repetitions:F4} ms/call, {allocation / repetitions} bytes/call; {count} spans observed.");
}
