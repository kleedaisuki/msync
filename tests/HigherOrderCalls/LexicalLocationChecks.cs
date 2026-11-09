using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class LexicalLocationChecks
{
    public static async Task<int> RunAsync()
    {
        (string Name, string Source, Value Expected)[] programs =
        [
            ("standalone-nullable-map", """
                let values?: Range<Number> = [1, 2];
                if values is null { "none" } else {
                    $std::string::join($std::ranges::map(values, fn(value: Number) {
                        (value * 2) as String
                    }), "|")
                }
                """, new StringValue("2|4")),
            ("private-nullable-index", """
                fn select(flag: Bool) {
                    let index?: Number = if flag { 1 } else { null };
                    if index is null { ["none"] } else {
                        $std::ranges::take(["a", "b"], index)
                    }
                };
                $std::string::join(select(true), "|")
                """, new StringValue("a")),
            ("captured-unwritten-local", """
                fn build() {
                    let values?: Range<Number> = [3, 4];
                    scope {
                        read: fn {
                            if values is null { ["none"] } else {
                                $std::ranges::map(values, fn(value: Number) { value as String })
                            }
                        },
                    }
                };
                let record = build();
                $std::string::join(record::read(), "|")
                """, new StringValue("3|4")),
            ("private-shadowed-writers", """
                fn select() {
                    let values?: Range<Number> = [1, 2];
                    { let values?: Range<Number> = null; values = [7]; };
                    let writer = fn(values?: Range<Number>) { values = null; };
                    writer([5]);
                    for values in [[8]] { values = [9]; };
                    let state = scope { values?: [6], clear: fn { values = null; } };
                    state::clear();
                    if values is null { ["none"] } else {
                        $std::ranges::map(values, fn(value: Number) { value as String })
                    }
                };
                $std::string::join(select(), "|")
                """, new StringValue("1|2")),
            ("loop-private-lifetime", """
                let total = 0;
                for source in [1, 2] {
                    let count?: Number = source;
                    if count is not null {
                        total = total + $std::ranges::size($std::ranges::take(["a", "b"], count));
                    };
                };
                total
                """, new NumberValue(3)),
            ("match-capture-lifetime", """
                fn select(flag: Bool) {
                    let original? = if flag { [4] } else { null };
                    match original {
                        selected => {
                            if selected is null { ["none"] } else {
                                $std::ranges::map(selected, fn(value: Number) { value as String })
                            }
                        },
                    }
                };
                $std::string::join($std::ranges::concat(select(true), select(false)), "|")
                """, new StringValue("4|none")),
            ("later-local-shadow", """
                fn select() {
                    let values?: Range<Number> = [1];
                    let clear = fn { values = null; };
                    let values?: Range<Number> = [2];
                    clear();
                    if values is null { ["none"] } else {
                        $std::ranges::map(values, fn(value: Number) { value as String })
                    }
                };
                $std::string::join(select(), "|")
                """, new StringValue("2")),
            ("mutable-module-field", """
                let module = $std::bool;
                let pending = $std::ranges::map([true], fn(value: Bool) { module::identity(value) });
                module::identity = fn(value: Bool) { not value };
                $std::ranges::first(pending)
                """, Value.False),
        ];
        (string Name, string Source)[] rejected =
        [
            ("private-later-write", """
                fn bad() {
                    let values?: Range<Number> = [1];
                    if values is null { [0] } else {
                        let pending = $std::ranges::map(values, fn(value: Number) { value });
                        values = null;
                        pending
                    }
                };
                $std::ranges::size(bad())
                """),
            ("private-captured-writer", """
                fn bad() {
                    let values?: Range<Number> = [1];
                    let clear = fn { global values; values = null; };
                    if values is null { [0] } else {
                        let pending = $std::ranges::map(values, fn(value: Number) { value });
                        clear();
                        pending
                    }
                };
                $std::ranges::size(bad())
                """),
            ("public-scope-field", """
                let record = scope { values?: [1] };
                if record::values is null { 0 } else {
                    let pending = $std::ranges::map(record::values, fn(value: Number) { value });
                    record::values = null;
                    $std::ranges::size(pending)
                }
                """),
        ];
        int checks = 0;
        foreach (int level in new[] { 0, 2, 3 })
        {
            foreach (var test in programs)
            {
                Module source = Compiler.Compile(test.Source, test.Name + ".msync", level);
                foreach (Module image in new[] { source, RoundTrip(source), RoundTrip(Binding.Bind(source,
                    new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
                {
                    Value actual = await Interpreter.ExecuteAsync(image);
                    Require(actual == test.Expected, $"{test.Name} O{level}: expected {test.Expected}, got {actual}.");
                }
            }
            foreach (var test in rejected) Reject(test.Source, level, test.Name);
            CompilerSession compiler = new(optimization: level);
            await using ExecutionSession session = new();
            async Task<Value> Submit(string text)
            {
                Value result = await session.ExecuteAsync(compiler.Compile(text));
                compiler.AcceptSubmission(session.GetLocalValue);
                return result;
            }
            await Submit("let values?: Range<Number> = [1, 2];");
            try
            {
                compiler.Compile("if values is not null { $std::ranges::map(values, fn(value: Number) { value }) };");
                throw new InvalidOperationException("A persistent REPL root was treated as a closed lifetime.");
            }
            catch (CompileException error) when (error.Diagnostic.Code == "E0203") { checks++; }
            await Submit("values = null;");
            Require(await Submit("values") is NullValue, "REPL root mutation was frozen.");
            Require(await Submit("""
                fn private_values() {
                    let local?: Range<Number> = [1, 2];
                    if local is null { [0] } else {
                        $std::ranges::map(local, fn(value: Number) { value })
                    }
                };
                $std::ranges::size(private_values())
                """) is NumberValue { Data: 2 }, "REPL private function locals lost their closed lifetime.");
            Require(compiler.AnalyzeEditor("""
                fn preview() {
                    let local?: Range<Number> = [1];
                    if local is not null { $std::ranges::map(local, fn(value: Number) { value }) }
                };
                """).Diagnostic is null, "REPL editor lost private-local narrowing.");
        }
        foreach (var test in programs)
            Require(EditorCompiler.Analyze(test.Source, test.Name + "-editing.msync").Diagnostic is null,
                "Standalone editor lost local location proofs: " + test.Name);
        return checks;

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        void Reject(string text, int level, string name)
        {
            try { Compiler.Compile(text, name + ".msync", level); }
            catch (CompileException error) when (error.Diagnostic.Code == "E0203") { checks++; return; }
            throw new InvalidOperationException("Unsafe deferred location accepted: " + name);
        }
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
