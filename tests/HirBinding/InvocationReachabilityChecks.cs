using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class InvocationReachabilityChecks
{
    internal static void Run()
    {
        int checks = 0;
        Check("captured-class", """
            scope Tools {
                entry: fn(value: Number) { Tools::leaf(value) },
                leaf: fn(value: Number) { value },
                unused: fn { Tools::entry(1) },
            };
            Tools::entry(1)
            """, [false, false, false]);
        Check("direct", "fn recur(value: Number) { if value == 0 { 0 } else { recur(value - 1) } }; recur(2)", [true]);
        Check("mutual", """
            scope Recursive {
                first: fn(value: Number) { if value == 0 { 0 } else { Recursive::second(value - 1) } },
                second: fn(value: Number) { Recursive::first(value) },
            };
            Recursive::first(2)
            """, [true, true]);
        Check("native-callback", """
            fn recur(values: Range) {
                $std::ranges::transform(values, fn(value) { recur(values) })
            };
            recur([1])
            """, [true, true]);
        Check("unknown", "fn invoke(operation: Function) { operation() }; invoke", [true]);
        Console.WriteLine($"PASS {checks} invocation reachability contracts");

        void Check(string name, string source, bool[] expected)
        {
            BoundProgram bound = new SourceBinder().Bind(source, name + ".msync");
            BoundCallableWrites graph = new(bound);
            HirTemplateId[] local = bound.Hir.Templates.Select((template, index) => (template, index))
                .Where(pair => pair.template.Source.Source.Value == 0).Select(pair => new HirTemplateId(pair.index)).ToArray();
            if (local.Length != expected.Length) throw new InvalidOperationException($"{name}: unexpected template count.");
            for (int index = 0; index < local.Length; index++)
            {
                bool result = graph.MayReenter(local[index]);
                if (result != expected[index] || graph.MayReenter(local[index]) != result)
                    throw new InvalidOperationException($"{name}: incorrect recursion domain for template {index}.");
                checks++;
            }
        }
    }
}
