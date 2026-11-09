using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

internal static class SingletonChecks
{
    public static async Task RunAsync()
    {
        (string Source, string? ExpectedPath)[] cases =
        [
            ("let path?:Path=./one; fn make(){scope Saved{item:{path=null;1}}}; make(); path=./again; make(); let result:Path=path; result", "./again"),
            ("let path?:Path=./one; fn make(){scope Saved{item:{path=null;1}}}; if exists ./singleton_probe {make()}; make(); let result?:Null=path; result is null", null),
            ("let path?:Path=./one; fn make(){scope Saved{item:{path=null;1}}}; decltype(make()); make(); let result?:Null=path; result is null", null),
            ("let path?:Path=./one; fn make(){path=./before; scope Saved{item:{path=null;1}}}; make(); path=./again; make(); let result:Path=path; result", "./before"),
            ("let path?:Path=./one; fn make(){scope Saved{item:{path=null;1}}; path=./after}; make(); path=./again; make(); let result:Path=path; result", "./after")
        ];
        int checks = 0;
        foreach (int level in new[] { 0, 2, 3 })
        {
            for (int index = 0; index < cases.Length; index++)
            {
                var bound = new SourceBinder().Bind(cases[index].Source, "singleton.msync");
                HirModule typed = new SemanticAnalyzer(bound).Analyze();
                Module code = Optimizer.Optimize(HirLowerer.Lower(typed).Module, level);
                using MemoryStream bytes = new();
                Artifact.Write(code, bytes);
                bytes.Position = 0;
                Value actual = await Interpreter.ExecuteAsync(Artifact.Read(bytes));
                bool passed = cases[index].ExpectedPath is { } expected
                    ? actual is PathValue path && path.Raw == expected
                    : actual is BoolValue { Data: true };
                if (!passed) throw new InvalidOperationException($"Singleton scenario {index + 1} O{level}");
                checks++;
            }
        }
        Console.WriteLine($"PASS {checks} singleton initialization contracts");
    }
}
