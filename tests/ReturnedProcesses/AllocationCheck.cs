using System.Reflection;
using System.Runtime.Loader;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class AllocationCheck
{
    internal static async Task Run(string baseline)
    {
        Module module = Compiler.Compile("fn increment(value: Number) { value + 1 };\n"
            + string.Join('\n', Enumerable.Repeat("increment(1);", 255)) + "\nincrement(1)", "owner-allocation.msync", 0);
        int calls = module.Functions.SelectMany(function => function.Instructions).Count(instruction => instruction.Op == OpCode.Call);
        if (calls != 256) throw new InvalidOperationException($"Expected 256 real calls, found {calls}.");
        using SharedCoreContext context = new();
        Assembly before = context.LoadFromAssemblyPath(Path.GetFullPath(baseline));
        MethodInfo oldExecute = before.GetType("Msync.Runtime.Interpreter", throwOnError: true)!.GetMethods()
            .Single(method => method.Name == "ExecuteAsync" && method.IsStatic);
        MethodInfo newExecute = typeof(Interpreter).GetMethods().Single(method => method.Name == "ExecuteAsync" && method.IsStatic);
        async Task<long> Measure(MethodInfo execute, Module code)
        {
            for (int warmup = 0; warmup < 32; warmup++) await Invoke(execute, code);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 256; iteration++) await Invoke(execute, code);
            return (GC.GetAllocatedBytesForCurrentThread() - allocated) / 256;
        }
        async ValueTask Invoke(MethodInfo execute, Module code)
        {
            var returned = (ValueTask<Value>)execute.Invoke(null, [code, null, CancellationToken.None])!;
            if (await returned is not NumberValue { Data: 2 }) throw new InvalidOperationException("Arithmetic result differs.");
        }
        long firstBefore = await Measure(oldExecute, module);
        long firstAfter = await Measure(newExecute, module);
        long secondAfter = await Measure(newExecute, module);
        long secondBefore = await Measure(oldExecute, module);
        Module control = Compiler.Compile("2", "owner-control.msync", 0);
        long controlBefore = await Measure(oldExecute, control);
        long controlAfter = await Measure(newExecute, control);
        Console.WriteLine($"256 calls, allocated bytes/entry ABBA: {firstBefore}, {firstAfter}, {secondAfter}, {secondBefore}");
        Console.WriteLine($"Zero-call control: {controlBefore} -> {controlAfter}; incremental 256-call bytes: {firstBefore - controlBefore} -> {firstAfter - controlAfter}");
    }

    private sealed class SharedCoreContext() : AssemblyLoadContext(isCollectible: true), IDisposable
    {
        protected override Assembly? Load(AssemblyName assemblyName) => assemblyName.Name == "Msync.Core" ? typeof(Module).Assembly : null;
        public void Dispose() => Unload();
    }
}
