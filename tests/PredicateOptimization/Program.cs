using Msync.Optimization;
using System.Reflection;
using System.Diagnostics;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

var method = typeof(Optimizer).Assembly.GetType("Msync.Optimization.PredicateOptimization")!.GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
var rewrite = method.CreateDelegate<Func<Module, FunctionCode, List<Value>, FunctionCode>>();
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "predicate-optimization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int contracts = 0;
void Require(bool result, string message)
{
    contracts++;
    if (!result) throw new InvalidOperationException(message);
}
Module Optimize(Module module)
{
    var constants = module.Constants.ToList();
    var functions = module.Functions.Select(function => rewrite(module, function, constants)).ToArray();
    return BlockBuilder.Form(module with { Constants = constants, Functions = functions });
}
async Task<string> Run(Module module)
{
    try
    {
        return (await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root))) switch
        {
            NumberValue number => "Number:" + BitConverter.DoubleToInt64Bits(number.Data).ToString("X16"),
            PathValue path => $"Path:{path.Raw}:{path.NativeText}:{path.Connection}:{path.WorkingDirectory}:{path.HomeDirectory}",
            var value => value.Type + ":" + value
        };
    }
    catch (MsyncException error) { return "error:" + error.Message; }
}
try
{
    if (args.Contains("--performance", StringComparer.Ordinal))
    {
        foreach (int count in new[] { 100, 500, 2000 })
        {
            var instructions = new List<Instruction>();
            var values = Enumerable.Range(0, count).Select(index => (Value)new NumberValue(index)).ToArray();
            for (int index = 0; index < count; index++) { instructions.Add(new(OpCode.Constant, index)); instructions.Add(new(OpCode.InitializeLocal, index)); }
            for (int index = 0; index < count; index++) { instructions.Add(new(OpCode.LoadLocal, index)); instructions.Add(new(OpCode.Pop)); }
            instructions.Add(new(OpCode.Null)); instructions.Add(new(OpCode.Return));
            Module module = new(values, [new("scaling", 0, count, instructions, TypeRef.Null)]);
            for (int warm = 0; warm < 3; warm++) Optimize(module);
            var times = new List<double>(); var allocations = new List<long>();
            for (int sample = 0; sample < 9; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                Optimize(module);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }
            times.Sort(); allocations.Sort();
            Console.WriteLine($"N={count}: {times[4]:F3} ms, {allocations[4]} bytes");
        }
        return;
    }
    var cases = new[]
    {
        "fn choose(flag) { if flag { if flag { 17 } else { 29 } } else { if flag { 31 } else { 41 } } }; choose(exists ./probe)",
        "let selected = if true { 7 } else { exists ./probe; 11 }; if selected == 7 { 17 } else { 29 }",
        "let file? = if exists ./probe { ./probe } else { null }; if file? { if file? { 17 } else { 29 } } else { if file? { 31 } else { 41 } }",
        "let file? = if exists ./probe { ./probe } else { null }; let empty = file?; if empty { if empty { 17 } else { 29 } } else { if empty { 31 } else { 41 } }",
        "let flag = exists ./probe; if flag { flag = false; if flag { 17 } else { 29 } } else { 41 }",
        "let flag = exists ./probe; let change = fn { flag = false; }; if flag { change(); if flag { 17 } else { 29 } } else { 41 }",
        "let file? = if exists ./probe { ./probe } else { null }; let change = fn { file = null; }; if file? { 17 } else { change(); if file? { 29 } else { 41 } }",
        "let result = 0; for value in [true, false, true] { if value { result = result + 1; } else { result = result + 3; }; }; result",
        "fn classify(value) { if value is Path { if value is Path { 17 } else { 29 } } else { 41 } }; classify(./probe)",
        "let value = exists ./probe; if value == true { if value == false { 17 } else { 29 } } else { if value { 31 } else { 41 } }",
        "fn keep(value) { if value == 0 { value } else { 1 } }; keep(-0)",
        "let value = if exists ./probe { 0 } else { -0 }; value",
        "fn keep(value) { if value == \"./probe\" { value } else { ./other } }; keep(./probe)"
    };
    foreach (var source in cases)
    {
        var original = Compiler.Compile(source, "predicate-case", 0);
        var optimized = Optimize(original);
        Artifact.Validate(optimized);
        foreach (var present in new[] { false, true })
        {
            var path = Path.Combine(root, "probe");
            if (present) File.WriteAllText(path, "actual selected branch"); else File.Delete(path);
            var expected = await Run(original);
            Require(await Run(optimized) == expected, source + ": selected behavior changed");
            using MemoryStream stream = new(); Artifact.Write(optimized, stream); stream.Position = 0;
            Require(await Run(Artifact.Read(stream)) == expected, source + ": serialized behavior changed");
            var executable = Binding.Bind(optimized, new(root, HostContext.HomeDirectory));
            Require(await Run(executable) == expected, source + ": bound behavior changed");
        }
    }
    var unknown = Compiler.Compile(cases[0], "predicate-unknown", 0);
    var reduced = Optimize(unknown);
    int Before() => unknown.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op is OpCode.JumpIfFalse or OpCode.JumpIfTrue));
    int After() => reduced.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op is OpCode.JumpIfFalse or OpCode.JumpIfTrue));
    Require(After() < Before(), "Dominating edge predicates were not propagated");
    Module rawAlias = new([], [new("raw-alias", 0, 2,
        [new(OpCode.True), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 10), new(OpCode.False), new(OpCode.StoreLocal, 1),
            new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.False), new(OpCode.Return)], TypeRef.Bool)]);
    Require(await Run(rawAlias) == "Bool:false", "Raw alias baseline does not write the shared location");
    Require(await Run(Optimize(rawAlias)) == "Bool:false", "Store through a raw capture alias retained a stale predicate");
    Module rangeType = new([new NumberValue(1), new TypeValue(new(TypeKind.Range, TypeRef.String)), new TypeValue(TypeRef.String)],
        [new("parameterized-type", 0, 1,
            [new(OpCode.Constant, 0), new(OpCode.MakeRange, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0),
                new(OpCode.Constant, 1), new(OpCode.IsType), new(OpCode.JumpIfTrue, 12), new(OpCode.LoadLocal, 0),
                new(OpCode.Constant, 2), new(OpCode.IsType), new(OpCode.Return), new(OpCode.Nop), new(OpCode.True), new(OpCode.Return)], TypeRef.Bool)]);
    Require(await Run(rangeType) == "Bool:false", "Parameterized type baseline changed");
    Require(await Run(Optimize(rangeType)) == "Bool:false", "Negative parameterized type edge excluded an entire Range kind");
    Module nanPayload = new([new NumberValue(BitConverter.Int64BitsToDouble(0x7ff8000000000001)),
        new NumberValue(BitConverter.Int64BitsToDouble(0x7ff8000000000002)), new NumberValue(1)],
        [new("nan-entry", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Number),
            new("nan-keep", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Equal), new(OpCode.JumpIfFalse, 6),
                new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.Constant, 2), new(OpCode.Return)], TypeRef.Number, ParameterTypes: [TypeRef.Number])]);
    Require(await Run(nanPayload) == "Number:7FF8000000000001", "NaN payload baseline changed");
    Require(await Run(Optimize(nanPayload)) == "Number:7FF8000000000001", "Numerical equality replaced the original NaN payload");
    Console.WriteLine($"Predicate optimization: {contracts} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }
