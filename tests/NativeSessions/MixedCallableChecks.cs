using Msync.Compiler;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;

internal static class MixedCallableChecks
{
    private sealed record Example(string Name, string Source, string Present, string Absent);

    internal static async Task Run()
    {
        List<Example> examples =
        [
            new("native alternatives through generic apply", """
                fn apply(action, value) { action(value) };
                let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase };
                apply(action, "TeXt")
                """, "text", "TEXT"),
            new("source/native alternatives", """
                let action = if exists ./condition { fn(value) { value } } else { $std::string::to_uppercase };
                action("TeXt")
                """, "TeXt", "TEXT"),
            new("heterogeneous closed results", """
                let action = if exists ./condition { fn(value: Number) { "source" } } else { $std::math::abs };
                let result = action(-7);
                if result is Number { result as String } else { result }
                """, "source", "7"),
            new("captured source environment", """
                let suffix = "-capture";
                let action = if exists ./condition { fn(value: String) { $std::string::concat(value, suffix) } } else { $std::string::to_uppercase };
                action("TeXt")
                """, "TeXt-capture", "TEXT"),
            new("callee captured before argument mutation", """
                let action = if exists ./condition { fn(value: String) { $std::string::concat(value, "-source") } } else { $std::string::to_uppercase };
                fn change() { action = $std::string::to_lowercase; "TeXt" };
                let first = action(change());
                $std::string::concat(first, $std::string::concat("|", action("TeXt")))
                """, "TeXt-source|text", "TEXT|text"),
            new("computed callee evaluated once", """
                let calls = 0;
                fn choose() {
                    calls = calls + 1;
                    if exists ./condition { fn(value) { value } } else { $std::string::to_uppercase }
                };
                fn argument() { calls = calls + 10; "TeXt" };
                let result = choose()(argument());
                $std::string::concat(result, calls as String)
                """, "TeXt11", "TEXT11"),
            new("closed builtin and namespace results", """
                let pick = if exists ./condition { fn(values) { $std::ranges::first(values) } }
                    else { $std::ranges::first };
                let item = scope {
                    number: pick([7]), text: pick(["native"]), flag: pick([true]), optional?: pick([null]),
                    path: pick([./entry]), endpoint: pick([$local]), type: pick([Number]),
                    operation: pick([fn(value) { value + 1 }]),
                    nested: pick([scope { number: 8 }]),
                    values: pick([[1, 2]]),
                };
                if item::number == 7 and item::text == "native" and item::flag and item::optional is null
                    and item::path is Path and item::endpoint is Connection
                    and item::type == Number and item::operation(3) == 4
                    and item::nested::number == 8 and $std::ranges::size(item::values) == 2 {
                    "all native"
                } else { "wrong" }
                """, "all native", "all native"),
            new("native callback adaptation", """
                let action = if exists ./condition { $std::ranges::transform }
                    else { fn(values, operation) { $std::ranges::transform(values, operation) } };
                $std::ranges::first(action([2], fn(value) { value + 1 })) as String
                """, "3", "3"),
            new("native callback captured writes", """
                let state = scope { number: 0 };
                let action = if exists ./condition { $std::ranges::for_each }
                    else { fn(values, operation) { $std::ranges::for_each(values, operation) } };
                action([1, 2], fn(value) { state::number = state::number + value; });
                state::number as String
                """, "3", "3")
        ];
        if (!OperatingSystem.IsWindows())
            examples.Add(new("live Process result", """
                let pick = if exists ./condition { fn(values) { $std::ranges::first(values) } }
                    else { $std::ranges::first };
                let child = $std::process::spawn("/bin/sh", ["-c", "printf child"]);
                $std::process::stdout($std::process::wait(pick([child])))
                """, "child", "child"));
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "mixed-callables-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int routes = 0;
        try
        {
            foreach (Example example in examples)
            foreach (int level in new[] { 0, 2 }) routes += await Check(example, level, root);
            foreach (int level in new[] { 0, 2 }) await CheckSession(root, level);
            Console.WriteLine($"PASS {routes} finite mixed callable source/detached execution routes");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<int> Check(Example example, int level, string directory)
    {
        NativeProgram[] images = Compile(example, level);
        int routes = 0;
        foreach (bool present in new[] { false, true })
        {
            string condition = Path.Combine(directory, "condition");
            if (present) await File.WriteAllTextAsync(condition, "selected");
            else File.Delete(condition);
            foreach (NativeProgram image in images)
            {
                await using NativeInterpreter interpreter = new(image, new(WorkingDirectory: directory));
                NativeValue result = await interpreter.ExecuteAsync();
                string expected = present ? example.Present : example.Absent;
                if (result.Kind != NativeKind.String || result.AsString() != expected)
                    throw new InvalidOperationException($"{example.Name} O{level} condition={present}: expected '{expected}', received '{result}'.");
                routes++;
            }
        }
        return routes;
    }

    private static NativeProgram[] Compile(Example example, int level)
    {
        NativeProgram program = Compiler.Compile(example.Source, example.Name + ".msync", level);
        try
        {
            using MemoryStream bytes = new();
            HirModule hir = Compiler.Analyze(example.Source, example.Name + ".msync");
            HirArtifact.Write(hir with { Sources = [.. hir.Sources.Select(source => source with { Text = null })] }, bytes);
            bytes.Position = 0;
            NativeProgram standalone = Compiler.LowerInstructions(HirArtifact.Read(bytes), level);
            return [program, Detached(program), standalone, Detached(standalone)];
        }
        catch (Exception error)
        {
            string failure = Path.Combine(Environment.CurrentDirectory, ".temp", "mixed-callables-failed-ir");
            File.WriteAllText(failure + ".txt", NativeArtifact.Disassemble(program));
            File.WriteAllText(failure + ".json", NativeArtifact.ToJson(program));
            throw new InvalidOperationException($"{example.Name} O{level}: source-free HIR/native artifact route failed.", error);
        }
    }

    private static NativeProgram Detached(NativeProgram program)
    {
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        return NativeArtifact.Read(bytes);
    }

    private static async Task CheckSession(string directory, int level)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "condition"), "session");
        CompilerSession compiler = new(optimization: level);
        await using NativeExecutionSession execution = new(new(WorkingDirectory: directory));
        async Task<NativeValue> Submit(string source)
        {
            NativeValue result = await execution.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(execution.GetLocalValue);
            return result;
        }
        await Submit("let action = if exists ./condition { fn(value) { value } } else { $std::string::to_uppercase }; fn apply(value: String) { action(value) };");
        if ((await Submit("apply(\"TeXt\")")).AsString() != "TeXt")
            throw new InvalidOperationException("A mixed source callable lost its accepted session target.");
        await Submit("action = $std::string::to_lowercase;");
        if ((await Submit("apply(\"TeXt\")")).AsString() != "text")
            throw new InvalidOperationException("A compatible native reassignment lost its established session protocol.");
        await Submit("let mapper = if exists ./condition { $std::ranges::filter } else { fn(values, operation) { $std::ranges::filter(values, operation) } }; fn mapped(values, operation) { mapper(values, operation) }; fn keep(value: Number) { value > 1 };");
        if ((await Submit("$std::ranges::size(mapped([1, 2, 3], keep))")).AsNumber() != 2)
            throw new InvalidOperationException("A mixed native callback lost its session specialization.");
        await Submit("mapper = $std::ranges::take_while;");
        if ((await Submit("$std::ranges::size(mapped([1, 2, 3], keep))")).AsNumber() != 0)
            throw new InvalidOperationException("A reassigned native callback lost its established session specialization.");
    }

}
