using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;
using Msync.Optimization;

internal static class AggregateChecks
{
    public static async Task RunAsync()
    {
        int checks = 0;
        async Task<Value> Run(string text, int level)
        {
            var bound = new SourceBinder().Bind(text, "aggregate.msync");
            HirModule typed = new SemanticAnalyzer(bound).Analyze();
            Module code = Optimizer.Optimize(HirLowerer.Lower(typed).Module, level);
            using MemoryStream bytes = new();
            Artifact.Write(code, bytes);
            bytes.Position = 0;
            return await Interpreter.ExecuteAsync(Artifact.Read(bytes));
        }
        void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }
        foreach (int level in new[] { 0, 2, 3 })
        {
            Check(await Run("1 + 2", level) is NumberValue { Data: 3 }, $"Typed scalar lowering O{level}");
            Check(await Run("scope { item: 7 }", level) is ScopeValue record
                && record["item"] is NumberValue { Data: 7 }, $"Scope constructor O{level}");
            Check(await Run("let left=scope {item:1}; let right=left; right::item=4; left::item", level)
                is NumberValue { Data: 4 }, $"Scope aliases O{level}");
            Check(await Run("scope Family { read:fn {Family::later}, later:4 }; Family::read()", level)
                is NumberValue { Data: 4 }, $"Named self capture O{level}");
            Check(await Run("fn make(value){ scope { item:value } }; let record=make(3); record::item", level)
                is NumberValue { Data: 3 }, $"Generic record O{level}");
            var values = (RangeValue)await Run("let value=1; let values=[value,value+1]; value=3; values", level);
            List<double> actual = [];
            await foreach (var value in values.Items) actual.Add(((NumberValue)value).Data);
            Check(actual.SequenceEqual(new[] { 1d, 4d }), $"Value and delayed location captures O{level}");
            Check(await Run("let value=1; decltype(value=2); value", level) is NumberValue { Data: 1 },
                $"Unevaluated operand O{level}");
            Check(await Run("fn use(operations){ operations::get() }; let first=scope{ get:fn{1} }; let second=scope{ get:fn{2} }; use(first)*10+use(second)", level)
                is NumberValue { Data: 12 }, $"Structural callable identity O{level}");
            Check(await Run("let action=fn{1}; fn read(){action()}; let first=read(); action=fn{2}; first*10+read()", level)
                is NumberValue { Data: 12 }, $"A live captured callable is resolved from its location O{level}");
            Check(await Run("let value=1; fn write(){value=2}; write(); value", level)
                is NumberValue { Data: 2 }, $"Caller-visible capture writes O{level}");
            Check(await Run("$std::bool::both(true,false)", level) is BoolValue { Data: false },
                $"A source module uses the shared HIR import location O{level}");
            Check(await Run("fn make(value){scope Saved {item:value}; Saved}; make(\"first\"); let second=make(./path); second::item", level)
                is StringValue { Data: "first" }, $"A named Scope retains its established carrier O{level}");
        }
        Console.WriteLine($"PASS {checks} typed aggregate contracts");
    }
}
