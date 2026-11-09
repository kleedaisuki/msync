using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

var pass = typeof(Optimizer).Assembly.GetType("Msync.Core.InterproceduralSpecialization")!
    .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
var assertions = 0;
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "ipa-specialization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ControlAndArgumentEffects();
    await UnusedArgumentFailure();
    await Captures();
    await MutableParameter();
    await NativeIdentity();
    await CaptureLayoutMismatch();
    await DynamicTypeFailure();
    await ScopeMutation();
    await RawCaptureMutation();
    await EscapingCallback();
    await SignedZero();
    await CloneBudget();
    await Product();
    Console.WriteLine($"Interprocedural specialization: {assertions} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }

void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

Module Specialize(Module module, int level = 2) => (Module)pass.Invoke(null, [module, level])!;
Module Source(string text) => Compiler.Compile(text, "ipa-test", 0);

Module RoundTrip(Module module)
{
    using MemoryStream stream = new(); Artifact.Write(module, stream); stream.Position = 0;
    return Artifact.Read(stream);
}

async Task<string> Outcome(Module module, string directory)
{
    using StringWriter errors = new();
    try
    {
        var result = await Interpreter.ExecuteAsync(module with { OptimizationLevel = 0 }, new(WorkingDirectory: directory, Error: errors));
        return await Snapshot(result) + "|" + errors;
    }
    catch (MsyncException error) { return "error:" + error.Message + ":" + error.Span; }
}

async Task<string> Snapshot(Value value)
{
    if (value is NumberValue number) return $"Number:{BitConverter.DoubleToInt64Bits(number.Data):x16}";
    if (value is not RangeValue range) return $"{value.Type}:{value}";
    var items = new List<string>();
    await foreach (var item in range.Items) items.Add(await Snapshot(item));
    return "Range:[" + string.Join(',', items) + "]";
}

async Task<string> Check(string name, Module before, Module after, bool files = false)
{
    var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
    var expected = await Outcome(before, directory);
    var outputs = Directory.EnumerateFiles(directory).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
    foreach (var candidate in new[] { after, RoundTrip(after), RoundTrip(Binding.Bind(after with { OptimizationLevel = 0 }, new(directory, directory))) })
    {
        if (files) foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Require(await Outcome(candidate, directory) == expected, name + ": result differs");
        if (!files) continue;
        var actual = Directory.EnumerateFiles(directory).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        Require(outputs.Count == actual.Count, name + ": file count differs");
        foreach (var output in outputs) Require(actual.TryGetValue(output.Key, out var bytes) && bytes.AsSpan().SequenceEqual(output.Value), name + ": bytes differ: " + output.Key);
    }
    return expected;
}

async Task ControlAndArgumentEffects()
{
    var source = Source("fn write(mode: Bool, unused, destination: Path) { if mode { \"chosen\" -> destination } else { \"other\" -> destination }; }; write(true, (\"argument\" -> ./argument), ./first); write(true, false, ./second);");
    var changed = Specialize(source);
    Require(changed.Functions.Count == source.Functions.Count + 1, "Equal control values must share one clone despite differing unused arguments");
    var clone = changed.Functions[^1];
    Require(clone.ParameterCount == 1, "Constant control and unused input arguments should be removed");
    Require(clone.Instructions.Any(instruction => instruction.Op == OpCode.Return), "Clone preserves invocation return boundary");
    Require(changed.Functions[0].Instructions.Count(instruction => instruction.Op == OpCode.Call && instruction.B == changed.Functions.Count) == 2, "Both direct calls select the clone");
    await Check("control-effects", source, changed, files: true);
    Require(File.ReadAllText(Path.Combine(root, "control-effects", "argument")) == "argument", "Removed argument still evaluates its effect");
    Require(File.ReadAllText(Path.Combine(root, "control-effects", "first")) == "chosen", "Known true mode selects expected branch");
}

async Task UnusedArgumentFailure()
{
    FunctionCode callee = new("ignore", 1, 1, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number, ParameterTypes: [TypeRef.Any]);
    Module source = new([new NumberValue(7)], [new("main", 0, 0,
        [new(OpCode.MakeClosure, 1), new(OpCode.MakeScope, 0), new(OpCode.GetMember, Text: "missing"), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Number), callee], OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(changed.Functions[^1].ParameterCount == 0, "Unused formal should disappear");
    Require((await Check("argument-failure", source, changed)).StartsWith("error:", StringComparison.Ordinal), "Argument failure remains selected");
}

async Task Captures()
{
    FunctionCode callee = new("captured", 2, 3,
        [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 4), new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.Null), new(OpCode.Return)], TypeRef.Any,
        CaptureCount: 1, ParameterTypes: [TypeRef.Bool, TypeRef.Number]);
    Module source = new([new NumberValue(29), new NumberValue(99)], [new("main", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.True), new(OpCode.Constant, 1), new(OpCode.Call, 2, 2), new(OpCode.Return)], TypeRef.Any), callee], OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(changed.Functions[^1].CaptureCount == 1 && changed.Functions[^1].ParameterCount == 0, "Capture prefix survives formal pruning");
    Require(await Check("capture-layout", source, changed) == "Number:403d000000000000|", "Actual closure capture is retained");
}

async Task MutableParameter()
{
    var source = Source("fn choose(mode: Bool) { mode = false; if mode { 1 } else { 2 } }; choose(true)");
    var changed = Specialize(source);
    Require(changed.Functions.Count == source.Functions.Count, "Mutated parameter must not be replaced by its incoming value");
    await Check("mutable-parameter", source, changed);
}

async Task NativeIdentity()
{
    FunctionCode callee = new("unused-argument", 1, 1, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Any]);
    Module source = new([new FunctionValue(-1, NativeName: "fs.exists"), new PathValue("./absent")],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Bool), callee], OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(ReferenceEquals(source, changed), "Call.B must not override a native callee identity");
    Require(await Check("native-identity", source, changed) == "Bool:false|", "Native operation remains actual target");
}

async Task ScopeMutation()
{
    var source = Source("let box = scope { choose: fn(mode: Bool) { if mode { \"old\" } else { \"old false\" } } }; let update = fn { box::choose = fn(mode: Bool) { if mode { \"new\" } else { \"new false\" } }; }; update(); box::choose(true)");
    Require(await Check("scope-mutation", source, Specialize(source)) == "String:new|", "Unknown call invalidates mutable scope member identity");
}

async Task DynamicTypeFailure()
{
    FunctionCode callee = new("dynamic-not", 1, 1,
        [new(OpCode.LoadLocal, 0), new(OpCode.Not), new(OpCode.JumpIfFalse, 5), new(OpCode.True), new(OpCode.Return), new(OpCode.False), new(OpCode.Return)],
        TypeRef.Bool, ParameterTypes: [TypeRef.Any]);
    Module source = new([new NumberValue(7)], [new("main", 0, 0,
        [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Bool), callee], OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(ReferenceEquals(source, changed), "Unknown storage types cannot become an invalid literal-typed body");
    Require((await Check("dynamic-type-failure", source, changed)).StartsWith("error:", StringComparison.Ordinal), "Runtime type error remains selected rather than moving to artifact validation");
}

async Task CaptureLayoutMismatch()
{
    FunctionCode callee = new("ignore", 1, 1, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool]);
    FunctionCode carrier = new("carrier", 0, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1);
    Module source = new([], [new("main", 0, 0,
        [new(OpCode.False), new(OpCode.MakeClosure, 2, 1), new(OpCode.True), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Bool), callee, carrier],
        OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(ReferenceEquals(source, changed), "Direct-B capture layout mismatch keeps original frame semantics");
    Require(await Check("capture-layout-mismatch", source, changed) == "Bool:true|", "Extra actual captures are overwritten by original argument slot");
}

async Task SignedZero()
{
    var source = Source("fn keep(value: Number) { if value == 0 { value } else { 1 } }; keep(- 0)");
    var changed = Specialize(source);
    Require(await Check("signed-zero", source, changed) == "Number:8000000000000000|", "Constant cloning preserves signed zero representation");
    foreach (var bits in new[] { long.MinValue, 0x7ff8000000000011L })
    {
        FunctionCode function = new("keep", 1, 1,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Equal), new(OpCode.JumpIfFalse, 6), new(OpCode.LoadLocal, 0), new(OpCode.Return), new(OpCode.Constant, 0), new(OpCode.Return)],
            TypeRef.Number, ParameterTypes: [TypeRef.Number]);
        Module original = new([new NumberValue(0), new NumberValue(BitConverter.Int64BitsToDouble(bits))],
            [new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 1), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Number), function], OptimizationLevel: 0);
        var specialized = Specialize(original);
        Require(specialized.Functions[^1].ParameterCount == 0, "Exact primitive representation is actually specialized");
        Require(await Check("representation-" + bits.ToString("x16"), original, specialized) == $"Number:{bits:x16}|", "Value cloning preserves every Number payload bit");
    }
}

async Task EscapingCallback()
{
    var source = Source("fn choose(mode: Bool) { if mode { \"true\" } else { \"false\" } }; choose(true); $std::ranges::transform([true, false], choose)");
    var changed = Specialize(source);
    Require(changed.Functions.Count > source.Functions.Count, "Known direct call receives its own constant clone");
    Require(await Check("escaping-callback", source, changed) == "Range:[String:true,String:false]|", "Escaping callback keeps its original signature and callable identity");
}

async Task RawCaptureMutation()
{
    FunctionCode Choose(string name, int literal) => new(name, 1, 1,
        [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 4), new(OpCode.Constant, literal), new(OpCode.Return), new(OpCode.Constant, literal), new(OpCode.Return)],
        TypeRef.String, ParameterTypes: [TypeRef.Bool]);
    Module source = new([new StringValue("old"), new StringValue("new")],
        [new("main", 0, 3,
            [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 3, 1), new(OpCode.InitializeLocal, 1),
             new(OpCode.LoadLocal, 1), new(OpCode.GetCapture, 0), new(OpCode.InitializeLocal, 2), new(OpCode.MakeClosure, 2), new(OpCode.StoreLocal, 2),
             new(OpCode.LoadLocal, 0), new(OpCode.True), new(OpCode.Call, 1), new(OpCode.Return)], TypeRef.String),
         Choose("old", 0), Choose("new", 1), new("carrier", 0, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1)],
        OptimizationLevel: 0);
    var changed = Specialize(source);
    Require(await Check("raw-capture-mutation", source, changed) == "String:new|", "GetCapture alias mutation invalidates the original callable binding");
}

async Task CloneBudget()
{
    var text = "fn choose(mode: Number) { if mode == 0 { exists ./first } else { exists ./second } }; "
        + string.Join(' ', Enumerable.Range(0, 64).Select(index => $"choose({index});"));
    var source = Source(text); var changed = Specialize(source, 3);
    Require(changed.Functions.Count <= source.Functions.Count + 2, "Code-growth budget bounds distinct value variants");
    Require(Specialize(changed, 3).Functions.Count == changed.Functions.Count, "Repeated optimizer rounds do not replenish cloning budget");
    await Check("clone-budget", source, changed);
}

async Task Product()
{
    var source = Source("fn write(mode: Bool, unused, destination: Path) { if mode { \"chosen\" -> destination } else { \"other\" -> destination }; }; write(true, (\"argument\" -> ./argument), ./first); write(true, false, ./second);");
    foreach (var level in new[] { 2, 3 })
    {
        var changed = Optimizer.Optimize(source, level);
        var clone = changed.Functions.SingleOrDefault(function => function.Name.StartsWith("write$ipa(", StringComparison.Ordinal));
        Require(clone is not null && clone.ParameterCount == 1, "Product O" + level + " selects reduced effectful clone");
        Require(!clone!.Instructions.Any(instruction => instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse), "Product removes cloned constant control");
        Require(!changed.Functions.Any(function => function.Name == "write"), "Unescaped original body is not retained just for its callable address");
        Require(!changed.Constants.OfType<StringValue>().Any(value => value.Data == "other"), "Unselected clone branch leaves no residual constant");
        await Check("product-control-effects-" + level, source, changed, files: true);
    }
}
