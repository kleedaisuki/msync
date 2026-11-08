using System.Reflection;
using System.Diagnostics;
using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

MethodInfo scalarize = typeof(Optimizer).Assembly.GetType("Msync.Core.ScopeScalarization")!
    .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
Module Scalarize(Module module)
{
    var constants = module.Constants.ToList();
    return module with
    {
        Functions = module.Functions.Select(function => (FunctionCode)scalarize.Invoke(null, [module, function, constants])!).ToArray(),
        Constants = constants,
        Blocks = null
    };
}

string Snapshot(Value value) => value switch
{
    ScopeValue scope => "{" + string.Join(',', scope.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + Snapshot(pair.Value))) + "}",
    _ => value.Type + ":" + value
};

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "scope-scalarization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    async Task<string> Execute(Module module)
    {
        try { return Snapshot(await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root))); }
        catch (MsyncException exception) { return "error:" + exception.Message; }
    }

    foreach ((string name, string source, bool eliminated) in new[]
    {
        ("write-read", "let value = exists ./flag; let box = scope { item: false }; box::item = value; box::item", true),
        ("alias-write", "let box = scope { item: 0 }; let alias = box; alias::item = 9; box::item", true),
        ("last-write", "let box = scope { item: 0 }; box::item = 4; box::item = 9; box::item", true),
        ("escaped-return", "let box = scope { item: 1 }; box::item = 9; box", false),
        ("escaped-read", "let box = scope { item: 1 }; box::item = 9; let seen = box::item; scope { object: box, seen: seen }", false),
        ("captured-object", "let box = scope { item: 0 }; let mutate = fn { box::item = 9; }; mutate(); box::item", false),
        ("captured-rebinding", "let box = scope { item: 1 }; let other = scope { item: 2 }; let replace = fn { box = other; }; replace(); box::item = 9; other::item", false),
        ("captured-field-store", "scope Box { item: 1, update: fn { item = 7; } }; Box::update(); Box::item", true),
        ("captured-field-read", "scope Box { item: 1, read: fn { item } }; Box::item = 9; Box::read()", true),
        ("branch-alias", "let box = scope { item: 0 }; if exists ./flag { box::item = 9; }; box::item", false),
        ("loop-liveout", "let previous = scope { item: 0 }; for item in [1, 2] { let box = scope { item: item }; previous = box; }; previous::item", false),
        ("rebound-alias", "let box = scope { item: 1 }; let alias = box; alias = scope { item: 2 }; alias::item = 9; box::item", true)
    })
    {
        Module original = Compiler.Compile(source, name, 0);
        Module transformed = Scalarize(original);
        if (eliminated)
        {
            Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.MakeScope), name + ": private object still allocated");
            Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.GetMember or OpCode.SetMember)), name + ": private field operations remain");
        }
        Module twice = Scalarize(transformed);
        Require(twice.Functions.SelectMany(function => function.Instructions).SequenceEqual(transformed.Functions.SelectMany(function => function.Instructions)), name + ": scalarization is not idempotent");
        foreach (bool present in new[] { false, true })
        {
            string flag = Path.Combine(root, "flag");
            if (present) await File.WriteAllTextAsync(flag, "present");
            else File.Delete(flag);
            string expected = await Execute(original);
            Require(await Execute(transformed) == expected, name + ": scalarized result differs");
            foreach (Module candidate in new[] { transformed, Binding.Bind(transformed, new(root, root)) })
            {
                Artifact.Validate(candidate);
                using MemoryStream stream = new();
                Artifact.Write(candidate, stream); stream.Position = 0;
                Require(await Execute(Artifact.Read(stream)) == expected, name + ": serialized result differs");
            }
        }
    }
    Module cellAliases = new([new NumberValue(1), new NumberValue(2)], [new("cells", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.InitializeLocal, 0),
         new(OpCode.LoadCell, 0), new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 1), new(OpCode.MakeScope, 1, Text: "item"),
         new(OpCode.StoreLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "item"), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
    Require(await Execute(Scalarize(cellAliases)) == "Number:2", "raw capture-cell alias store did not update original binding");
    Module fieldAliases = new([new NumberValue(1), new NumberValue(2)], [new("fields", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.Pop),
         new(OpCode.LoadStorage, 0), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.InitializeLocal, 1),
         new(OpCode.LoadLocal, 1), new(OpCode.Constant, 1), new(OpCode.SetMember, Text: "item"), new(OpCode.Pop),
         new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
    Require(await Execute(Scalarize(fieldAliases)) == "Number:2", "scalar field store lost shared captured location");
    Module captureAliases = new([new NumberValue(10), new NumberValue(20)],
        [new("capture-alias", 0, 4,
            [new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "value"), new(OpCode.InitializeLocal, 0),
             new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, 1),
             new(OpCode.LoadLocal, 1), new(OpCode.GetCapture, 0), new(OpCode.InitializeLocal, 2),
             new(OpCode.LoadStorage, 2), new(OpCode.MakeScope, 1, Text: "item"), new(OpCode.InitializeLocal, 3),
             new(OpCode.LoadLocal, 3), new(OpCode.Constant, 1), new(OpCode.MakeScope, 1, Text: "value"),
             new(OpCode.SetMember, Text: "item"), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "value"), new(OpCode.Return)], TypeRef.Number),
         new("captured", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Scope, CaptureCount: 1)], OptimizationLevel: 0);
    Require(await Execute(Scalarize(captureAliases)) == "Number:20", "GetCapture field alias lost original mutable location");
    Module invalid = new([], [new("missing", 0, 0,
        [new(OpCode.MakeScope), new(OpCode.GetMember, Text: "missing"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)], OptimizationLevel: 0);
    Require(await Execute(Scalarize(invalid)) == await Execute(invalid), "missing member diagnostic was removed");
    Random random = new(80119);
    for (int sample = 0; sample < 24; sample++)
    {
        int[] values = [0, 1]; int alias = 0; int observed = 0;
        StringBuilder source = new("let left = scope { item: 0 }; let right = scope { item: 1 }; let alias = left; let observed = 0;\n");
        for (int step = 0; step < 64; step++)
        {
            if (random.Next(3) == 0)
            {
                alias = random.Next(2); source.Append("alias = ").Append(alias == 0 ? "left" : "right").AppendLine(";");
            }
            else
            {
                int selected = random.Next(3), value = random.Next(100);
                string target = selected switch { 0 => "left", 1 => "right", _ => "alias" };
                values[selected == 2 ? alias : selected] = value;
                source.Append(target).Append("::item = ").Append(value).AppendLine(";");
            }
            observed += values[alias]; source.AppendLine("observed = observed + alias::item;");
        }
        source.Append("observed + left::item + right::item");
        Module transformed = Scalarize(Compiler.Compile(source.ToString(), "alias-property", 0));
        Require(await Execute(transformed) == "Number:" + (observed + values[0] + values[1]), "generated alias memory versions differ from independent scalar model");
        Require(transformed.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.MakeScope or OpCode.GetMember or OpCode.SetMember)), "generated private objects were not fully scalarized");
    }
    if (args.Contains("--performance", StringComparer.Ordinal))
    {
        foreach (int count in new[] { 100, 500, 2000 })
        {
            StringBuilder source = new("let dynamic = exists ./flag;\n");
            for (int index = 0; index < count; index++)
                source.Append($"let box{index} = scope {{ item: false }}; box{index}::item = dynamic; let read{index} = box{index}::item;\n");
            Module input = Compiler.Compile(source.ToString(), "scope-scaling", 0);
            for (int warm = 0; warm < 3; warm++) Scalarize(input);
            var times = new List<double>(); var allocations = new List<long>();
            for (int sample = 0; sample < 9; sample++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                Module result = Scalarize(input);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                Require(result.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.MakeScope), "scaling scope not scalarized");
            }
            times.Sort(); allocations.Sort();
            Console.WriteLine($"{count} instances: {times[4]:F3} ms, {allocations[4] / 1_000_000.0:F3} MB allocation");
        }
    }
    Console.WriteLine($"Scope scalarization: {assertions} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }
