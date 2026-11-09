using Msync.Optimization;
using System.Globalization;
using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
void Equal(string expected, Value value, string name) => Require(Snapshot(value) == expected, $"{name}: expected '{expected}', received '{Snapshot(value)}'");
string Snapshot(Value value) => value switch
{
    NullValue => "Null:null",
    StringValue text => "String:" + text.Data,
    NumberValue number => "Number:" + number.Data.ToString(CultureInfo.InvariantCulture),
    BoolValue boolean => "Bool:" + boolean.Data.ToString().ToLowerInvariant(),
    PathValue path => "Path:" + path.Raw,
    _ => value.Type + ":" + value
};
RuntimeContext context = new(new(WorkingDirectory: Environment.CurrentDirectory));
ValueTask<Value> Native(string name, params Value[] values) => NativeRegistry.InvokeAsync("string." + name, values, context,
    (_, _, _) => throw new InvalidOperationException("This text operation must not invoke callbacks directly"));
async Task RangeEqual(string[] expected, Value value, string name)
{
    Require(value is RangeValue { ItemType.Kind: TypeKind.String }, name + ": expected Range<String>");
    List<string> actual = [];
    await foreach (Value item in ((RangeValue)value).Items) actual.Add(((StringValue)item).Data);
    Require(actual.SequenceEqual(expected), $"{name}: expected [{string.Join('|', expected)}], received [{string.Join('|', actual)}]");
}
async Task Reject(string name, params Value[] values)
{
    try { _ = await Native(name, values); }
    catch (MsyncException) { assertions++; return; }
    throw new InvalidOperationException(name + ": invalid scalar index/count was accepted");
}

StringValue text = new("A😀e\u0301");
Equal("Number:4", await Native("length", text), "Unicode scalar length");
Equal("Number:8", await Native("utf8_size", text), "UTF-8 byte length");
Equal("Bool:false", await Native("is_ascii", text), "ASCII classification");
Equal("Bool:true", await Native("is_ascii", new StringValue("A\0z")), "ASCII control characters");
await RangeEqual(["A", "😀", "e", "\u0301"], await Native("chars", text), "Unicode scalar iteration");
Equal("String:😀e", await Native("substring", text, new NumberValue(1), new NumberValue(2)), "Scalar substring");
Equal("String:e\u0301", await Native("substring", text, new NumberValue(2), new NumberValue(1e30)), "Huge count clamps without a quota");
Equal("String:", await Native("substring", text, new NumberValue(4), new NumberValue(1)), "Substring at end");
Equal("String:😀", await Native("char_at", text, new NumberValue(1)), "Scalar character lookup");
Equal("Number:1", await Native("find", text, new StringValue("😀")), "Find scalar offset");
Equal("Number:3", await Native("rfind", new StringValue("A😀B😀"), new StringValue("😀")), "Reverse scalar offset");
Equal("Number:4", await Native("rfind", text, new StringValue("")), "Reverse empty pattern at end");
Equal("Null:null", await Native("find", text, new StringValue("missing")), "No-match null");
Equal("Number:1", await Native("compare", new StringValue("\U00010000"), new StringValue("\uE000")), "Scalar rather than UTF-16 order");
Equal("String:ababab", await Native("repeat", new StringValue("ab"), new NumberValue(3)), "Repeat");
Equal("String:", await Native("repeat", new StringValue(""), new NumberValue(1e30)), "Empty repetition does not allocate count slots");
await RangeEqual([], await Native("lines", new StringValue("")), "Empty text has no lines");
await RangeEqual(["", "A", "B"], await Native("lines", new StringValue("\r\nA\nB\r")), "Portable line endings");
await RangeEqual(["A", "😀", "e\u0301"], await Native("split_whitespace", new StringValue(" \tA\u2003😀\ne\u0301 ")), "Unicode whitespace");
Equal("String:A|😀|e|\u0301", await Native("join", await Native("chars", text), new StringValue("|")), "Streaming string join");
foreach (double invalid in new[] { -1.0, 0.5, double.NaN, double.PositiveInfinity })
{
    await Reject("repeat", new StringValue(""), new NumberValue(invalid));
    await Reject("substring", text, new NumberValue(invalid), new NumberValue(0));
}
await Reject("substring", text, new NumberValue(5), new NumberValue(0));
await Reject("char_at", text, new NumberValue(4));

Random random = new(452);
string[] alphabet = ["a", " ", "汉", "😀", "Ω", "e", "\u0301", "\U00010000", "\uE000"];
for (int sample = 0; sample < 30; sample++)
{
    string[] scalars = Enumerable.Range(0, random.Next(1, 13)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
    string value = string.Concat(scalars);
    int start = random.Next(scalars.Length + 1), count = random.Next(scalars.Length + 4);
    Equal("Number:" + scalars.Length, await Native("length", new StringValue(value)), "Generated scalar length");
    Equal("String:" + string.Concat(scalars.Skip(start).Take(count)),
        await Native("substring", new StringValue(value), new NumberValue(start), new NumberValue(count)), "Generated scalar substring");
}

if (args.Contains("--native-only", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {assertions} native Unicode text contracts");
    return;
}

(string Source, string Expected)[] cases =
[
    ("$std::string::length(\"A😀é\")", "Number:4"),
    ("$std::string::join($std::string::chars(\"A😀é\"), \"|\")", "String:A|😀|e|́"),
    ("$std::string::join($std::string::lines(\"A\nB\n\"), \"|\")", "String:A|B"),
    ("$std::string::slice(\"A😀BC\", 1, 3)", "String:😀B"),
    ("$std::string::back(\"A😀\")", "String:😀"),
    ("$std::string::take_last(\"A😀BC\", 3)", "String:😀BC"),
    ("$std::string::drop(\"abc\", 9)", "String:"),
    ("$std::string::replace_first(\"a=b=c\", \"=\", \":\")", "String:a:b=c"),
    ("$std::string::replace_last(\"a=b=c\", \"=\", \":\")", "String:a=b:c"),
    ("$std::string::partition(\"a=b=c\", \"=\")::after", "String:b=c"),
    ("$std::string::rpartition(\"a=b=c\", \"=\")::before", "String:a=b"),
    ("$std::string::pad_left(\"😀\", 3)", "String:  😀"),
    ("$std::string::pad_right(\"😀\", 3)", "String:😀  "),
    ("$std::string::remove_prefix(\"prefix:value\", \"prefix:\")", "String:value"),
    ("$std::string::remove_suffix(\"value.txt\", \".txt\")", "String:value"),
    ("$std::string::drop_last(\"a😀bc\", 2)", "String:a😀"),
    ("$std::string::insert(\"a😀c\", 2, \"b\")", "String:a😀bc"),
    ("$std::string::erase(\"a😀bc\", 1, 2)", "String:ac"),
    ("$std::string::contains_ignore_case(\"Straße\", \"RAS\")", "Bool:true"),
    ("$std::string::try_at(\"abc\", 3)", "Null:null"),
    ("let position? = $std::string::find(\"A😀b\", \"b\"); if position? { 0 } else { position }", "Number:2"),
    ("$std::string::equal_ignore_case(\"Straße\", \"STRASSE\")", "Bool:true"),
    ("let calls = 0; let values = $std::ranges::transform([\"a\", \"b\"], fn(value: String) { calls = calls + 1; value }); let _ = $std::string::join(values, \",\"); calls", "Number:2"),
    ("let calls = 0; let values = $std::ranges::transform([\"a\", \"b\"], fn(value: String) { calls = calls + 1; value }); let _ = $std::string::join($std::ranges::take(values, 0), \",\"); calls", "Number:0"),
    ("$std::path::filename(./folder/name.txt @ ssh://unused)", "Path:name.txt"),
    ("$std::path::extension(./folder/name.txt @ ssh://unused)", "Path:.txt"),
    ("$std::path::normalize(./folder/../name.txt @ ssh://unused)", "Path:name.txt"),
    ("$std::path::with_extension(./folder/name.txt @ ssh://unused, \"md\")", "Path:./folder/name.md"),
    ("$std::path::native(./folder/name.txt @ ssh://unused)", "String:./folder/name.txt"),
    ("$std::path::dirname(./folder/name.txt @ ssh://unused)", "Path:./folder"),
    ("$std::path::has_extension(./folder/name.txt @ ssh://unused)", "Bool:true"),
    ("$std::path::generic_string($std::path::filename(./folder/\"one\\two\" @ ssh://unused))", "String:one\\two")
];
foreach (int level in new[] { 0, 2, 3 })
    foreach ((string source, string expected) in cases)
    {
        Module program = Frontend.Compile(source, "stdlib-text-path", level);
        foreach (Module stage in new[] { program, RoundTrip(program), RoundTrip(Binding.Bind(program, new(context.WorkingDirectory, context.HomeDirectory))) })
            Equal(expected, await Interpreter.ExecuteAsync(stage, new(WorkingDirectory: context.WorkingDirectory)), source + $" O{level} {stage.Stage}");
    }

foreach (int level in new[] { 0, 2, 3 })
    foreach (string source in new[]
    {
        "let _ = $std::string::repeat(\"\", \"0.5\" as Number);",
        "let _ = $std::string::char_at(\"😀\", 1);",
        "let _ = $std::string::pad_left(\"wide enough\", \"0.5\" as Number);"
    })
    {
        Module program = Frontend.Compile(source, "stdlib-selected-text-error", level);
        foreach (Module stage in new[] { program, RoundTrip(program), RoundTrip(Binding.Bind(program, new(context.WorkingDirectory, context.HomeDirectory))) })
        {
            try { _ = await Interpreter.ExecuteAsync(stage); }
            catch (MsyncException) { assertions++; continue; }
            throw new InvalidOperationException($"O{level} {stage.Stage} removed a selected text error: {source}");
        }
    }
foreach (int level in new[] { 0, 2, 3 })
    Equal("String:untouched", await Interpreter.ExecuteAsync(Frontend.Compile(
        "if false { let _ = $std::string::repeat(\"\", \"0.5\" as Number); }; \"untouched\"", "unselected-text-error", level)),
        "Unselected validation must not execute");
Console.WriteLine($"PASS: {assertions} Unicode, source composition, contextual Path and binary round-trip contracts");

static Module RoundTrip(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream); stream.Position = 0;
    return Artifact.Read(stream);
}
