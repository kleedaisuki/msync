using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;

internal static class MutableStateChecks
{
    public static async Task RunCurrentState()
    {
        Module current = new([new StringValue("before"), new StringValue("after")],
            [new FunctionCode("main", 0, 2,
            [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, 1), new(OpCode.MakeClosure, 1, 1),
             new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Call), new(OpCode.Return)], TypeRef.String)
             { CodeSignature = new([], 0, []) },
             new FunctionCode("read", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String, CaptureCount: 1)
             { CodeSignature = new([], 0, [new(0, true)]) }]) { CodeTypes = [new(TypeKind.String)] };
        foreach (int level in new[] { 0, 3 })
        {
            Module optimized = Reload(Optimizer.Optimize(current, level));
            Value relocatable = await Interpreter.ExecuteAsync(optimized);
            Equal("after", relocatable, $"current closure capture at O{level}");
            Module bound = Reload(Binding.Bind(optimized, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
            Value executable = await Interpreter.ExecuteAsync(bound);
            Equal("after", executable, $"bound current closure capture at O{level}");
        }
        await ConstantIsolation();
        Console.WriteLine("PASS: four source-free live captures and fresh mutable Scope constants");
    }

    private static async Task ConstantIsolation()
    {
        ScopeValue nested = new(new ScopeLayout(["value"], 0), [new StringValue("before")]);
        ScopeValue template = new(new ScopeLayout(["nested"], 1), [nested]);
        Module module = new([template], [new FunctionCode("main", 0, 0,
            [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Scope) { CodeSignature = new([], 2, []) }])
        {
            CodeTypes = [new(TypeKind.String), new(TypeKind.Scope, ScopeLayouts: [0]), new(TypeKind.Scope, ScopeLayouts: [1])],
            ScopeLayouts = [new([new("value", 0)]), new([new("nested", 1)])]
        };
        string cwd = Environment.CurrentDirectory;
        foreach (Module image in new[] { Reload(module), Reload(Binding.Bind(module, new(cwd, cwd))) })
        {
            ScopeValue first = (ScopeValue)await Interpreter.ExecuteAsync(image);
            ScopeValue firstNested = (ScopeValue)first.Members["nested"];
            firstNested.SetMember("value", new StringValue("changed"));
            ScopeValue second = (ScopeValue)await Interpreter.ExecuteAsync(image);
            if (ReferenceEquals(first, second) || ReferenceEquals(firstNested, second.Members["nested"]))
                throw new InvalidOperationException("Separate module executions shared mutable Scope storage");
            Equal("before", ((ScopeValue)second.Members["nested"]).Members["value"], "second Scope instance");
            Equal("before", ((ScopeValue)((ScopeValue)image.Constants[0]).Members["nested"]).Members["value"], "constant template");
        }
    }

    private static void Equal(string expected, Value actual, string context)
    {
        if (actual is not StringValue { Data: var text } || text != expected)
            throw new InvalidOperationException($"{context}: expected '{expected}', received '{actual}'");
    }

    private static Module Reload(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
