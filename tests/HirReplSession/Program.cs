using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int checks = 0;
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "hir-repl-session", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        string origin = Path.Combine(root, "origin-" + level);
        string next = Path.Combine(root, "空间 next-" + level);
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(next);
        CompilerSession compiler = new(optimization: level);
        await using ExecutionSession execution = new(new(WorkingDirectory: origin));

        async Task<Value> Submit(string source)
        {
            Module module;
            try { module = compiler.Compile(source); }
            catch { compiler.ReconcileSubmission(_ => false); throw; }
            try
            {
                Value result = await execution.ExecuteAsync(module);
                compiler.AcceptSubmission(execution.GetLocalValue);
                return result;
            }
            catch
            {
                compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                throw;
            }
        }

        void Check(bool valid, string reason)
        {
            checks++;
            if (!valid) throw new InvalidOperationException($"O{level}: {reason}");
        }

        await Submit("""
            fn Counter() {
                let state = scope { value: 0 };
                scope {
                    get: fn { state::value },
                    inc: fn { state::value = state::value + 1; },
                }
            };
            let counter = Counter();
            """);
        string[] names = compiler.BindingNames.Order(StringComparer.Ordinal).ToArray();
        for (int pass = 0; pass < 3; pass++)
        {
            EditorAnalysis analysis = compiler.AnalyzeEditor("""
                counter::inc();
                "not executed" -> ./analysis-must-not-write.txt;
                let proposed = counter::get();
                """, execution.GetLocalValue);
            Check(analysis.Diagnostic is null, "valid semantic analysis has no diagnostic");
            Check(compiler.BindingNames.Order(StringComparer.Ordinal).SequenceEqual(names),
                "analysis does not publish declarations into the accepted session");
            Check(!File.Exists(Path.Combine(origin, "analysis-must-not-write.txt")),
                "analysis does not perform file effects");
        }
        Check(await Submit("counter::get()") is NumberValue { Data: 0 },
            "analysis does not invoke captured mutation");
        Check(compiler.AnalyzeEditor("let invalid: Number = true;", execution.GetLocalValue).Diagnostic is not null,
            "failed analysis returns a diagnostic");
        Check(!compiler.BindingNames.Contains("invalid"), "failed analysis does not publish a binding");
        Check(await Submit("let alias = counter; alias::inc(); counter::get()") is NumberValue { Data: 1 },
            "accepted feedback preserves Scope and captured-cell identity");

        await Submit("let shared = 0; fn bump() { global shared; shared = shared + 1; shared };");
        Check(await Submit("bump()") is NumberValue { Data: 1 }, "global import uses its definition location");
        await Submit("let shared = \"shadow\";");
        Check(await Submit("bump()") is NumberValue { Data: 2 }, "shadowing does not retarget an existing capture");
        Check(await Submit("shared") is StringValue { Data: "shadow" }, "the new lexical binding remains selected");

        await Submit("let text = \"before\"; fn once(value: String) { [value] }; let delayed = once(text);");
        await Submit("text = \"after\";");
        Check(await Submit("$std::ranges::first(delayed)") is StringValue { Data: "after" },
            "deferred arguments read their shared location on demand");

        await Submit("let values?: Range<Number> = [1];");
        Check(await Submit("if values is not null { $std::ranges::size(values) } else { 0 }") is NumberValue { Data: 1 },
            "current non-null facts apply to an immediate read");
        await Submit("values = null;");
        Check(await Submit("if values is not null { $std::ranges::size(values) } else { 0 }") is NumberValue { Data: 0 },
            "future submissions retain nullable storage admission");

        try
        {
            await Submit("let retained = \"accepted prefix\"; let broken = \"not a number\" as Number;");
            throw new InvalidOperationException("The invalid runtime conversion unexpectedly succeeded.");
        }
        catch (MsyncException) { }
        Check(compiler.BindingNames.Contains("retained") && !compiler.BindingNames.Contains("broken"),
            "runtime reconciliation keeps only initialized new declarations");
        Check(await Submit("retained") is StringValue { Data: "accepted prefix" },
            "the accepted prefix remains usable after a failed submission");

        await Submit("let saved = ./value;");
        execution.ChangeWorkingDirectory(next);
        ScopeValue paths = (ScopeValue)await Submit("scope { saved: saved, current: ./value }");
        Check(paths["saved"] is PathValue saved && saved.WorkingDirectory == origin,
            "existing Path values retain their origin");
        Check(paths["current"] is PathValue current && current.WorkingDirectory == next,
            "new Path values use the session directory");
    }
    Console.WriteLine($"PASS {checks} persistent semantic-session contracts");
}
finally { Directory.Delete(root, recursive: true); }
