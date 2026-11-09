using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class NativeFamilies
{
    public static async Task RunAsync()
    {
        int checks = 0;
        (string Source, string Expected)[] cases =
        [
            ("let b = $std::text::StringBuilder::new(); $std::text::StringBuilder::build(b)", ""),
            ("let b = $std::text::StringBuilder::new(); let alias = b; $std::text::StringBuilder::append(alias, \"A😀\"); b::length", "2"),
            ("let b = $std::text::StringBuilder::new(); let fresh = $std::text::StringBuilder::new(); $std::text::StringBuilder::append(b, \"x\"); $std::text::StringBuilder::is_empty(fresh)", "true"),
            ("let b = $std::text::StringBuilder::new(); $std::text::StringBuilder::append(b, \"a\"); let snapshot = $std::text::StringBuilder::snapshot(b); $std::text::StringBuilder::append(b, \"b\"); snapshot", "a"),
            ("let b = $std::text::StringBuilder::new(); $std::text::StringBuilder::append_line(b, \"A😀\"); $std::text::StringBuilder::append_all(b, [\"b\", \"c\"]); $std::text::StringBuilder::build(b)", "A😀\nbc"),
            ("let b = $std::text::StringBuilder::new(); $std::text::StringBuilder::append(b, \"a\"); $std::text::StringBuilder::clear(b); $std::text::StringBuilder::len(b)", "0"),
            ("let p = $std::regex::Pattern::new(\"[0-9]+\"); $std::regex::Pattern::replace(p, \"a12b\", \"x\")", "axb"),
            ("let p = $std::regex::Pattern::new(\"😀\"); let found? = $std::regex::Pattern::find(p, \"A😀z\"); if found is null { -1 } else { found::index }", "1"),
            ("let p = $std::regex::Pattern::new(\"z\"); $std::regex::Pattern::find(p, \"abc\") is null", "true"),
            ("let p = $std::regex::Pattern::new(\"[,;]\"); $std::string::join($std::regex::Pattern::split(p, \"a,b;c\"), \"|\")", "a|b|c"),
            ("let p = $std::regex::Pattern::new(\"a\"); let alias = p; alias::expression = \"b\"; $std::regex::Pattern::is_match(p, \"b\")", "true"),
            ("let p = $std::regex::Pattern::new(\"😀\"); $std::ranges::size($std::regex::Pattern::matches(p, \"😀a😀\"))", "2"),
            ("let b = $std::text::Builder(); b::append(\"old\"); b::build()", "old"),
            ("let p = $std::regex::compile(\"[0-9]+\"); p::replace(\"a12b\", \"old\")", "aoldb"),
            ("""
                let calls = 0;
                scope EagerChunks {
                    append: fn(chunks: Range<String>, text: String) {
                        calls = calls + 1;
                        $std::ranges::materialize(
                            $std::ranges::concat(chunks, [text])
                        )
                    },
                    build: fn(chunks: Range<String>) {
                        $std::string::join(chunks, "")
                    },
                };
                let b = $std::text::StringBuilder::with_policy(EagerChunks);
                $std::text::StringBuilder::append_all(b, ["A", "😀"]);
                if calls == 2 and b::length == 2 {
                    $std::text::StringBuilder::build(b)
                } else { "bad policy invocation" }
                """, "A😀"),
        ];
        foreach (int level in new[] { 0, 2, 3 })
        {
            foreach (var (source, expected) in cases)
            {
                try
                {
                    Module module = Compiler.Compile(source, "native-text-pattern.msync", level);
                    foreach (Module stage in Stages(module))
                    {
                        Value actual = await Interpreter.ExecuteAsync(stage);
                        Require(actual.ToString() == expected,
                            $"O{level} {stage.Stage}: expected '{expected}', got '{actual}': {source}");
                    }
                }
                catch (MsyncException error)
                {
                    throw new InvalidOperationException($"O{level}: {source}", error);
                }
            }
            foreach (string source in new[]
            {
                "$std::text::StringBuilder::with_policy(scope { build: fn(chunks:Range<String>) { \"\" } });",
                "$std::text::StringBuilder::with_policy(scope { append: fn(chunks:Range<String>,text:String){chunks}, build: fn(chunks:Range<String>) { false } });",
                "$std::regex::Pattern::is_match(scope {expression: 4}, \"text\");",
                "let b = $std::text::StringBuilder::new(); b::length = \"wrong\";",
            })
            {
                try { Compiler.Compile(source, "native-text-pattern-invalid.msync", level); }
                catch (CompileException error) when (error.Diagnostic.Code is "E0214" or "E0203")
                {
                    checks++;
                    continue;
                }
                throw new InvalidOperationException("Expected typed policy/storage rejection: " + source);
            }
            Value builder = await Interpreter.ExecuteAsync(Compiler.Compile(
                "$std::text::StringBuilder::new()", "native-builder-layout.msync", level));
            Require(builder is ScopeValue { Count: 3 } state
                && state["chunks"] is RangeValue
                && state["length"] is NumberValue { Data: 0 }
                && state["policy"] is ScopeValue,
                "Builder allocated methods/accessors instead of a data record.");
            Value pattern = await Interpreter.ExecuteAsync(Compiler.Compile(
                "$std::regex::Pattern::new(\"x\")", "native-pattern-layout.msync", level));
            Require(pattern is ScopeValue { Count: 1 } specification
                && specification["expression"] is StringValue { Data: "x" },
                "Pattern allocated methods/accessors instead of a specification record.");
        }
        Console.WriteLine($"PASS {checks} native text/pattern family contracts");
        return;

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static IEnumerable<Module> Stages(Module module)
    {
        yield return module;
        yield return RoundTrip(module);
        yield return RoundTrip(Binding.Bind(module,
            new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
