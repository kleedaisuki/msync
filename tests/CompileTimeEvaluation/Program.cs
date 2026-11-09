using Msync.Optimization;
using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

var method = typeof(Optimizer).Assembly.GetType("Msync.Optimization.CompileTimeEvaluation")!
    .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "compile-time-evaluation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions = 0;
try
{
    foreach (var (source, expected) in new[]
    {
        ("let total = 0; for item in [" + string.Join(',', Enumerable.Range(1, 200)) + "] { total = total + item; }; total", "20100"),
        ("let total = 9007199254740992; for item in [1,1,1] { total = total + item; }; total", "9007199254740992"),
        ("let total = 0; for outer in [1,2,3] { for inner in [4,5] { total = total + outer * inner; }; }; total", "54"),
        ("let total = 0; for item in [1,2,3,4] { if item == 3 { total = total + 17; } else { total = total + item; }; }; total", "24"),
        ("let total = 7; for item in [] { total = 99; }; total", "7"),
        ("for item in [1,2,3] { if item == 2 { return 37; }; }; 19", "37"),
        ("let value = \"\"; for item in [\"a\",\"b\",\"c\"] { value = $std::string::concat(value,item); }; value", "abc")
    })
    {
        var baseline = Compiler.Compile(source, "constexpr", 0);
        var constants = baseline.Constants.ToList();
        var changed = baseline with { Functions = baseline.Functions.Select(function => Rewrite(baseline, function, constants)).ToArray(), Constants = constants };
        Artifact.Validate(changed);
        Require(await Outcome(baseline) == expected, "Incorrect independent source oracle: " + source);
        Require(await Outcome(changed) == expected, "CTFE changed sequential result: " + source);
        if (!source.Contains("$std", StringComparison.Ordinal))
            Require(changed.Functions[changed.EntryPoint].Instructions.Count == 2, "Known finite scalar loop retained dynamic instructions");
        foreach (var candidate in new[] { RoundTrip(changed), RoundTrip(Binding.Bind(changed, new(root, root))) })
            Require(await Outcome(candidate) == expected, "Artifact result changed");
        if (args.Contains("--integrated", StringComparer.Ordinal))
            foreach (var level in new[] { 2, 3 })
            {
                var integrated = Optimizer.Optimize(baseline, level);
                Require(await Outcome(integrated) == expected, "Integrated compile-time evaluation changed the result");
                Require(integrated.Functions[integrated.EntryPoint].Instructions.Count == 2, "Integrated O" + level + " retained known finite loop work");
            }
    }

    var effectSource = "for item in [./a,./b] { \"bytes\" -> item; }; true";
    var effect = Compiler.Compile(effectSource, "effect", 0);
    var effectConstants = effect.Constants.ToList();
    Require(ReferenceEquals(Rewrite(effect, effect.Functions[0], effectConstants), effect.Functions[0]), "Selected transfer evaluated at compile time");
    Require(!File.Exists(Path.Combine(root, "a")), "Compile-time transfer wrote bytes");
    var read = Compiler.Compile("let found = false; for item in [./late] { found = exists item; }; found", "read", 0);
    var readConstants = read.Constants.ToList();
    Require(ReferenceEquals(Rewrite(read, read.Functions[0], readConstants), read.Functions[0]), "Filesystem observation froze at compile time");

    var unbounded = new Module([], [new("infinite", 0, 0, [new(OpCode.Jump, 0)], TypeRef.Null)], OptimizationLevel: 0);
    Require(ReferenceEquals(Rewrite(unbounded, unbounded.Functions[0], []), unbounded.Functions[0]), "Unbounded loop was evaluated");
    var context = Compiler.Compile("let path = ./base; for item in [\"child\"] { path = path / item; }; path", "path", 0);
    var contextConstants = context.Constants.ToList();
    Require(ReferenceEquals(Rewrite(context, context.Functions[0], contextConstants), context.Functions[0]), "Unbound host-specific PathJoin evaluated");
    var bound = Binding.Bind(context, new(root, root)) with { OptimizationLevel = 0 };
    var boundConstants = bound.Constants.ToList();
    var boundResult = bound with { Functions = bound.Functions.Select(function => Rewrite(bound, function, boundConstants)).ToArray(), Constants = boundConstants, Blocks = null };
    Require(await Outcome(bound) == await Outcome(boundResult), "Bound endpoint grammar changed");
    Require(boundResult.Functions[0].Instructions.Count == 2, "Known bound path loop retained contextual work");
    if (args.Contains("--integrated", StringComparer.Ordinal))
    {
        var portable = Optimizer.Optimize(context, 2);
        var executable = Binding.Bind(portable, new(root, root));
        Require(await Outcome(executable) == await Outcome(bound), "Integrated executable projection changed");
        Require(executable.Functions[0].Instructions.Count == 2, "Integrated executable retained contextual pure work");
    }

    var retainedError = new Module([new NumberValue(1), new StringValue("a"), new StringValue(""), new StringValue("replacement")],
        [new("selected-error", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.MakeRange, 1), new(OpCode.GetIterator), new(OpCode.StoreLocal, 0),
         new(OpCode.IteratorNext, 0, 12), new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 1), new(OpCode.Constant, 2),
         new(OpCode.Constant, 3), new(OpCode.NativeCall, 3, Text: "string.replace"), new(OpCode.Pop),
         new(OpCode.Jump, 4), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)], OptimizationLevel: 0);
    Artifact.Validate(retainedError);
    Require(ReferenceEquals(Rewrite(retainedError, retainedError.Functions[0], retainedError.Constants.ToList()), retainedError.Functions[0]), "Selected runtime error became a compile-time result");
    var random = new Random(227916);
    for (var sample = 0; sample < 32; sample++)
    {
        var values = Enumerable.Range(0, random.Next(5, 40)).Select(_ => random.Next(-20, 21)).ToArray();
        var source = "let total = 9007199254740992; for item in [" + string.Join(',', values)
            + "] { if item > 0 { total = total + item; } else { total = total - item; }; }; total";
        var baseline = Compiler.Compile(source, "generated-finite", 0);
        var known = baseline.Constants.ToList();
        var evaluated = baseline with { Functions = baseline.Functions.Select(function => Rewrite(baseline, function, known)).ToArray(), Constants = known };
        var expected = 9007199254740992d;
        foreach (var value in values) expected = value > 0 ? expected + value : expected - value;
        var actual = await Interpreter.ExecuteAsync(evaluated with { OptimizationLevel = 0 }, new(WorkingDirectory: root));
        Require(actual is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected), "Exact sequential IEEE result changed");
        Require(evaluated.Functions[0].Instructions.Count == 2, "Generated known loop retained dynamic work");
    }
    Console.WriteLine($"Compile-time evaluation: {assertions} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }

FunctionCode Rewrite(Module module, FunctionCode function, List<Value> constants) =>
    (FunctionCode)method.Invoke(null, [module, function, constants])!;
void Require(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
async Task<string> Outcome(Module module) => (await Interpreter.ExecuteAsync(module with { OptimizationLevel = 0 }, new(WorkingDirectory: root))).ToString()!;
Module RoundTrip(Module module)
{
    using MemoryStream stream = new(); Artifact.Write(module, stream); stream.Position = 0; return Artifact.Read(stream);
}
