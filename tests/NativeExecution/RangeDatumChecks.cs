using System.Globalization;
using Msync.Compiler;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class RangeDatumChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        Case[] cases =
        [
            new("materialize", "original", "$std::ranges::front(values)", "7"),
            new("front-before-ready", "original", "selected", "7", "let selected = $std::ranges::front(original);"),
            new("at-before-ready", "original", "selected", "7", "let selected = $std::ranges::at(original, 0);"),
            new("back-before-ready", "original", "selected", "11", "let selected = $std::ranges::back(original);"),
            new("size-before-ready", "original", "selected", "2", "let selected = $std::ranges::size(original);"),
            new("empty-before-ready", "original", "selected", "false", "let selected = $std::ranges::empty(original);"),
            new("find-if-before-ready", "original", "selected", "7", "let selected = $std::ranges::find_if(original, fn(value: Number) { true });"),
            new("any-before-ready", "original", "selected", "false", "let selected = $std::ranges::any_of(original, fn(value: Number) { false });"),
            new("all-before-ready", "original", "selected", "true", "let selected = $std::ranges::all_of(original, fn(value: Number) { true });"),
            new("none-before-ready", "original", "selected", "true", "let selected = $std::ranges::none_of(original, fn(value: Number) { false });"),
            new("sort-single", "$std::ranges::sort([child::exit_code])", "$std::ranges::front(values)", "7"),
            new("unique-single", "$std::ranges::unique([child::exit_code])", "$std::ranges::front(values)", "7"),
            new("merge-empty", "$std::ranges::merge(original, $std::ranges::empty_of(Number))", "$std::ranges::front(values)", "7"),
            new("difference-empty", "$std::ranges::set_difference(original, $std::ranges::empty_of(Number))", "$std::ranges::front(values)", "7"),
            new("reverse", "$std::ranges::reverse(original)", "$std::ranges::front(values)", "11"),
            new("take", "$std::ranges::take(original, 1)", "$std::ranges::front(values)", "7"),
            new("drop", "$std::ranges::drop(original, 1)", "$std::ranges::front(values)", "11"),
            new("stride", "$std::ranges::stride(original, 2)", "$std::ranges::front(values)", "7"),
            new("concat", "$std::ranges::concat(original, [13])", "$std::ranges::back(values)", "13"),
            new("flatten", "$std::ranges::flatten([original, [13]])", "$std::ranges::front(values)", "7"),
            new("transform", "$std::ranges::transform(original, fn(value: Number) { value + 1 })", "$std::ranges::front(values)", "8"),
            new("filter", "$std::ranges::filter(original, fn(value: Number) { true })", "$std::ranges::front(values)", "7"),
            new("take-while", "$std::ranges::take_while(original, fn(value: Number) { true })", "$std::ranges::front(values)", "7"),
            new("drop-while", "$std::ranges::drop_while(original, fn(value: Number) { false })", "$std::ranges::front(values)", "7"),
            new("zip", "$std::ranges::zip(original, [3, 5])", "$std::ranges::front(values)::first", "7"),
            new("enumerate", "$std::ranges::enumerate_from(original, 4)", "$std::ranges::front(values)::value", "7"),
            new("chunk", "$std::ranges::chunk(original, 2)", "$std::ranges::front($std::ranges::front(values))", "7"),
            new("slide", "$std::ranges::slide(original, 2)", "$std::ranges::front($std::ranges::front(values))", "7"),
            new("scan", "$std::ranges::scan(original, 0, fn(acc: Number, value: Number) { acc + value })", "$std::ranges::back(values)", "18"),
            new("sort-callback", "$std::ranges::sort_with(original, fn(left: Number, right: Number) { false })", "$std::ranges::front(values)", "7"),
            new("merge-callback", "$std::ranges::merge_with(original, [13], fn(left: Number, right: Number) { false })", "$std::ranges::front(values)", "7"),
            new("union-callback", "$std::ranges::set_union_with(original, [13], fn(left: Number, right: Number) { false })", "$std::ranges::front(values)", "7"),
            new("intersection-callback", "$std::ranges::set_intersection_with(original, [13], fn(left: Number, right: Number) { false })", "$std::ranges::front(values)", "7"),
            new("difference-callback", "$std::ranges::set_difference_with([child::exit_code], [13], fn(left: Number, right: Number) { false })", "$std::ranges::size(values)", "0"),
            new("symmetric-callback", "$std::ranges::set_symmetric_difference_with([child::exit_code], [13], fn(left: Number, right: Number) { false })", "$std::ranges::size(values)", "0"),
            new("of", "$std::ranges::of(child::exit_code, 11)", "$std::ranges::front(values)", "7"),
            new("single", "$std::ranges::single(child::exit_code)", "$std::ranges::front(values)", "7"),
            new("repeat", "$std::ranges::repeat(child::exit_code, 2)", "$std::ranges::back(values)", "7")
        ];
        int checks = 0;
        foreach (Case item in cases)
            foreach (bool pending in OperatingSystem.IsWindows() ? new[] { false } : new[] { false, true })
            {
                string directory = Path.Combine(root, "range-" + item.Name + (pending ? "-pending" : "-ready"));
                Directory.CreateDirectory(directory);
                File.Delete(Path.Combine(directory, "ready"));
                string init = pending ? item.Range : item.Range.Replace("child::exit_code", "7", StringComparison.Ordinal);
                string launch = pending ? "let child = release /bin/sh(\"-c\", \"while [ ! -f ready ]; do sleep 0.01; done; exit 7\");" : "";
                string original = pending ? "[child::exit_code, 11]" : "[7, 11]";
                string source = $$"""
                    {{launch}}
                    let original = {{original}};
                    {{item.Before ?? ""}}
                    let values = $std::ranges::materialize({{init}});
                    original = [99];
                    $std::io::write_text(./ready, "go");
                    $std::io::println(({{item.Result}}) as String);
                    """;
                string file = Path.Combine(directory, "program.msync");
                await File.WriteAllTextAsync(file, source);
                StringWriter output = new(CultureInfo.InvariantCulture), errors = new(CultureInfo.InvariantCulture);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
                await using NativeInterpreter interpreter = new(Compiler.CompileNative(source, file), new(directory, file, Output: output, Error: errors));
                await interpreter.ExecuteAsync(timeout.Token);
                if (output.ToString().Trim() != item.Expected || errors.ToString().Length != 0 || interpreter.FailedTransfers != 0)
                    throw new Exception($"{item.Name} pending={pending}: expected {item.Expected}, output={output}, errors={errors}");
                checks++;
            }
        Console.WriteLine($"PASS datum-range: {checks} real ready/pending structural/callback pipelines freeze membership without demanding payload");
        return checks;
    }

    private sealed record Case(string Name, string Range, string Result, string Expected, string? Before = null);
}
