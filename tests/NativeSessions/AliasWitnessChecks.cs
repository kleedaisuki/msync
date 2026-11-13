using Msync.Compiler;
using Msync.Runtime.Native;

internal static class AliasWitnessChecks
{
    internal static async Task Run()
    {
        foreach (bool legalFirst in new[] { true, false })
        {
            CompilerSession compiler = new();
            await using NativeExecutionSession execution = new(new(WorkingDirectory: Environment.CurrentDirectory));
            await Submit("""
                fn alter(view, original) {
                    view::Item = String;
                    let Item = original::Item;
                    let value: Item = 7;
                    value
                };
                """);
            if (legalFirst) await Legal();
            bool rejected = false;
            try
            {
                await Submit("""
                    let alias_a = scope { Item: Number };
                    let alias_b = scope { Item: Number };
                    let alias_view? = $std::ranges::first([alias_a, alias_b]);
                    if alias_view is null { 0 } else { alter(alias_view, alias_a) }
                    """);
            }
            catch (CompileException error)
            {
                rejected = error.Message.Contains("requires a statically known Type value", StringComparison.Ordinal);
            }
            if (!rejected) throw new InvalidOperationException("An aliased call reused a disjoint static Type witness.");
            await Legal();

            async Task Legal()
            {
                var value = await Submit("""
                    let distinct_a = scope { Item: Number };
                    let distinct_b = scope { Item: Number };
                    let distinct_original = scope { Item: Number };
                    let distinct_view? = $std::ranges::first([distinct_a, distinct_b]);
                    if distinct_view is null { 0 } else { alter(distinct_view, distinct_original) }
                    """);
                if (value.AsNumber() != 7) throw new InvalidOperationException("An alias failure poisoned a distinct object's Type witness.");
            }

            async Task<Msync.Core.Native.NativeValue> Submit(string source)
            {
                try
                {
                    var value = await execution.ExecuteAsync(compiler.Compile(source));
                    compiler.AcceptSubmission(execution.GetLocalValue);
                    return value;
                }
                catch
                {
                    compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                    throw;
                }
            }
        }
        Console.WriteLine("native alias Type witnesses: disjoint/aliased cache order and failed-submission recovery passed");
    }
}
