using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class SessionOptimizationChecks
{
    public static async Task Run()
    {
        int[] arithmeticCounts = new int[4];
        foreach (int level in new[] { 0, 2, 3 })
        {
            CompilerSession compiler = new(optimization: level);
            await using ExecutionSession execution = new();
            async Task<Value> Submit(string source)
            {
                Module image = compiler.Compile(source);
                try
                {
                    Value result = await execution.ExecuteAsync(image);
                    compiler.AcceptSubmission(execution.GetLocalValue);
                    return result;
                }
                catch
                {
                    compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                    throw;
                }
            }
            await Submit("fn arithmetic(value: Number) { value + 2 * 3 };");
            Module arithmetic = compiler.Compile("arithmetic(1)");
            arithmeticCounts[level] = arithmetic.Functions.Single(function => function.Name == "arithmetic"
                && function.ReturnType == TypeRef.Number).Instructions.Count;
            Console.WriteLine($"O{level} arithmetic instructions={arithmeticCounts[level]}");
            if (await execution.ExecuteAsync(arithmetic) is not NumberValue { Data: 7 })
                throw new InvalidOperationException("Arithmetic optimization changed the result.");
            compiler.AcceptSubmission(execution.GetLocalValue);
            await Submit("fn identity(value) { value };");
            List<int> counts = [];
            for (int index = 0; index < 8; index++)
            {
                Module image = compiler.Compile("identity(1)");
                counts.Add(image.Functions.Count);
                Value result = await execution.ExecuteAsync(image);
                if (result is not NumberValue { Data: 1 }) throw new InvalidOperationException("Repeated call failed.");
                compiler.AcceptSubmission(execution.GetLocalValue);
            }
            Console.WriteLine($"O{level} repeated function counts={string.Join(',',counts)}");
            if (counts.Distinct().Count() != 1) throw new InvalidOperationException("A repeated semantic specialization grew code.");
            if (compiler.AnalyzeEditor("identity(\"editor-only\")", execution.GetLocalValue).Diagnostic is not null)
                throw new InvalidOperationException("Valid editor specialization failed.");
            try { compiler.Compile("identity(\"uncommitted\"); let invalid: Number = true;"); }
            catch (CompileException) { compiler.ReconcileSubmission(_ => false); }
            Module afterFailure = compiler.Compile("identity(2)");
            if (afterFailure.Functions.Count != counts[0]) throw new InvalidOperationException("Rejected generation retained code promises.");
            await execution.ExecuteAsync(afterFailure);
            compiler.AcceptSubmission(execution.GetLocalValue);
            Module stringCall = compiler.Compile("identity(\"actual\")");
            if (stringCall.Functions.Count != counts[0] + 1) throw new InvalidOperationException("A new type did not create one specialization.");
            await execution.ExecuteAsync(stringCall);
            compiler.AcceptSubmission(execution.GetLocalValue);
            Module repeatString = compiler.Compile("identity(\"again\")");
            if (repeatString.Functions.Count != stringCall.Functions.Count) throw new InvalidOperationException("A String specialization was not reused.");
            await execution.ExecuteAsync(repeatString);
            compiler.AcceptSubmission(execution.GetLocalValue);
            await Submit("fn Counter() { let state = scope { value: 0 }; scope { inc: fn { state::value = state::value + 1; }, get: fn { state::value } } }; let counter = Counter();");
            await Submit("counter::inc();");
            await Submit("counter::inc();");
            if (await Submit("counter::get()") is not NumberValue { Data: 2 }) throw new InvalidOperationException("Shared specialization lost a captured Scope alias.");
            await Submit("let second_counter = Counter(); second_counter::inc();");
            if (await Submit("second_counter::get()") is not NumberValue { Data: 1 }
                || await Submit("counter::get()") is not NumberValue { Data: 2 })
                throw new InvalidOperationException("A cached factory reused another object's private state.");
            Console.WriteLine($"O{level} code-cache rollback/new-type/captured Scope PASS");
            if (await Submit("let object = scope { value: 1 }; let alias = object; alias::value = 2; object::value")
                is not NumberValue { Data: 2 }) throw new InvalidOperationException("Scope alias broke.");
            try { await Submit("let retained = 3; let broken = \"bad\" as Number;"); }
            catch (MsyncException) { }
            if (!compiler.BindingNames.Contains("retained") || compiler.BindingNames.Contains("broken"))
                throw new InvalidOperationException("Partial failure exported an uninitialized root.");
            Console.WriteLine($"O{level} alias/partial-failure PASS");
        }
        if (arithmeticCounts[2] >= arithmeticCounts[0] || arithmeticCounts[3] > arithmeticCounts[2])
            throw new InvalidOperationException("Session O2/O3 did not apply the existing optimizer.");
        Console.WriteLine("PASS focused session optimization and code reuse journeys");
    }
}
