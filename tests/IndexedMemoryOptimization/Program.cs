using System.Reflection;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--measure", StringComparer.Ordinal))
{
    await Measurements.Run();
    return;
}

MethodInfo pass = typeof(Optimizer).Assembly.GetType("Msync.Core.IndexedMemoryOptimization")!
    .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
int checks = 0;
BindingContext context = new(Environment.CurrentDirectory, Environment.CurrentDirectory);
List<(string Name, Func<Code> Program, double Expected, int Gets)> cases =
[
    ("repeat", Repeat, 20, 1),
    ("disjoint-index", Disjoint, 20, 1),
    ("same-index", SameIndex, 109, 2),
    ("length-value-partition", SizeAcrossStore, 4, 0),
    ("scope-alias", ScopeAlias, 2, 1),
    ("field-replacement", FieldReplacement, 109, 2),
    ("raw-cell-rebind", CellRebind, 109, 2),
    ("scope-cell-rebind", ScopeCellRebind, 109, 2),
    ("swap", Swap, 30, 2),
    ("unknown-index", UnknownIndex, 109, 3),
    ("length-resize", SizeAcrossResize, 5, 0),
    ("callback-mutation", CallbackMutation, 109, 2),
    ("barrier", Barrier, 20, 2),
    ("diamond-disjoint", Diamond, 20, 1),
    ("loop-iteration", Loop, 6, 1),
];
foreach (var item in cases)
{
    Code program = item.Program();
    FunctionCode original = program.Function();
    FunctionCode rewritten = Rewrite(original, program.Constants);
    Check(rewritten.Instructions.Count(instruction => instruction.Op == OpCode.NativeCall && instruction.Text == "storage.buffer_get") == item.Gets,
        item.Name + " residual get count");
    Check(Rewrite(rewritten, program.Constants).Instructions.SequenceEqual(rewritten.Instructions), item.Name + " idempotence");
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module raw = new(program.Constants, [original, .. program.Helpers], OptimizationLevel: 0);
        Module optimized = Optimizer.Optimize(raw, level);
        if (level >= 2)
        {
            Check(optimized.Functions.Sum(body => body.Instructions.Count(instruction => instruction.Op == OpCode.NativeCall
                && instruction.Text == "storage.buffer_get")) == item.Gets, item.Name + " public optimizer residual gets");
            if (item.Name == "length-value-partition") Check(optimized.Functions.Sum(body => body.Instructions.Count(instruction =>
                instruction.Op == OpCode.NativeCall && instruction.Text == "storage.buffer_size")) == 1, "public length reuse");
        }
        foreach (Module image in Images(optimized))
        {
            Value value = await Interpreter.ExecuteAsync(image);
            Check(value is NumberValue number && number.Data == item.Expected, $"{item.Name} O{level}: {value}");
        }
    }
}

foreach (bool resize in new[] { false, true })
{
    Code program = new(); program.Buffer(10);
    if (resize)
    {
        program.Get(0); program.Emit(OpCode.Pop);
        program.Emit(OpCode.LoadLocal, 0); program.Native("buffer_pop", 1); program.Emit(OpCode.Pop);
    }
    program.Get(resize ? 0 : 4, 71); program.Emit(OpCode.Pop);
    program.Get(resize ? 0 : 4, 99); program.Emit(OpCode.Return);
    FunctionCode rewritten = Rewrite(program.Function(), program.Constants);
    var gets = rewritten.Instructions.Where(instruction => instruction.Op == OpCode.NativeCall && instruction.Text == "storage.buffer_get").ToArray();
    Check(gets[0].Span?.Start == (resize ? 0 : 71), "first get failure provenance");
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(new(program.Constants, [program.Function()], OptimizationLevel: 0), level);
        try { await Interpreter.ExecuteAsync(optimized); throw new InvalidOperationException("Missing bounds failure"); }
        catch (MsyncException error)
        {
            Check(error.Span?.Start == 71, $"O{level}: wrong selected failure span: {error.Span}");
        }
    }
}

Code noBuffer = new(); noBuffer.Number(1); noBuffer.Emit(OpCode.Return);
FunctionCode noBufferFunction = noBuffer.Function();
Check(ReferenceEquals(Rewrite(noBufferFunction, noBuffer.Constants), noBufferFunction), "negative control identity");
Console.WriteLine($"PASS {checks} indexed memory contracts");

FunctionCode Rewrite(FunctionCode function, IReadOnlyList<Value> constants) => (FunctionCode)pass.Invoke(null, [function, constants])!;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
IEnumerable<Module> Images(Module module)
{
    yield return module;
    yield return RoundTrip(module);
    yield return RoundTrip(Binding.Bind(module, context));
}
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new(); Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}

Code Repeat()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code Disjoint()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.Set(1, 99); code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code SameIndex()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.Set(0, 99); code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code SizeAcrossStore()
{
    Code code = new(); code.Buffer(10, 20); code.Size(); code.Emit(OpCode.InitializeLocal, 1);
    code.Set(0, 99); code.Emit(OpCode.LoadLocal, 1); code.Size(); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code ScopeAlias()
{
    Code code = new(); code.Number(1); code.Emit(OpCode.MakeScope, 1, text: "x");
    code.Emit(OpCode.MakeRange, 1); code.Native("buffer_new", 1); code.Emit(OpCode.InitializeLocal, 0);
    code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.Emit(OpCode.LoadLocal, 1); code.Number(2); code.Emit(OpCode.SetMember, text: "x"); code.Emit(OpCode.Pop);
    code.Get(0); code.Emit(OpCode.GetMember, text: "x"); code.Emit(OpCode.Return); return code;
}
Code FieldReplacement()
{
    Code code = new(); code.Buffer(10); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.MakeScope, 1, text: "items");
    code.Emit(OpCode.InitializeLocal, 1); code.Buffer(99); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 2);
    code.Emit(OpCode.LoadLocal, 1); code.Emit(OpCode.GetMember, text: "items"); code.Number(0); code.Native("buffer_get", 2);
    code.Emit(OpCode.InitializeLocal, 3);
    code.Emit(OpCode.LoadLocal, 1); code.Emit(OpCode.LoadLocal, 2); code.Emit(OpCode.SetMember, text: "items"); code.Emit(OpCode.Pop);
    code.Emit(OpCode.LoadLocal, 3); code.Emit(OpCode.LoadLocal, 1); code.Emit(OpCode.GetMember, text: "items");
    code.Number(0); code.Native("buffer_get", 2); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code CellRebind()
{
    Code code = new(); code.Buffer(99); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 2); code.Buffer(10);
    code.Emit(OpCode.LoadCell, 0); code.Emit(OpCode.InitializeLocal, 1); code.Get(0); code.Emit(OpCode.InitializeLocal, 3);
    code.Emit(OpCode.LoadLocal, 2); code.Emit(OpCode.StoreLocal, 1); code.Emit(OpCode.LoadLocal, 3); code.Get(0);
    code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code ScopeCellRebind()
{
    Code code = new(); code.Buffer(99); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 2); code.Buffer(10);
    code.Emit(OpCode.LoadCell, 0); code.Emit(OpCode.MakeScope, 1, text: "items"); code.Emit(OpCode.InitializeLocal, 1);
    code.Get(0); code.Emit(OpCode.InitializeLocal, 3);
    code.Emit(OpCode.LoadLocal, 1); code.Emit(OpCode.LoadLocal, 2); code.Emit(OpCode.SetMember, text: "items"); code.Emit(OpCode.Pop);
    code.Emit(OpCode.LoadLocal, 3); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code Swap()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.Emit(OpCode.LoadLocal, 0); code.Number(0); code.Number(1); code.Native("buffer_swap", 3); code.Emit(OpCode.Pop);
    code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code UnknownIndex()
{
    Code code = new(); code.Buffer(0); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 2); code.Buffer(10, 20);
    code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.LoadLocal, 2); code.Number(0); code.Native("buffer_get", 2); code.Number(99);
    code.Native("buffer_set", 3); code.Emit(OpCode.Pop); code.Emit(OpCode.LoadLocal, 1); code.Get(0);
    code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code SizeAcrossResize()
{
    Code code = new(); code.Buffer(10, 20); code.Size(); code.Emit(OpCode.InitializeLocal, 1);
    code.Emit(OpCode.LoadLocal, 0); code.Number(99); code.Native("buffer_push", 2); code.Emit(OpCode.Pop);
    code.Emit(OpCode.LoadLocal, 1); code.Size(); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code CallbackMutation()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    int zero = code.Constant(new NumberValue(0)), changed = code.Constant(new NumberValue(99));
    code.Helpers.Add(new("mutate", 1, 2,
    [
        new(OpCode.LoadLocal, 0), new(OpCode.Constant, zero), new(OpCode.Constant, changed),
        new(OpCode.NativeCall, 3, Text: "storage.buffer_set"), new(OpCode.Pop), new(OpCode.True), new(OpCode.Return)
    ], TypeRef.Bool, CaptureCount: 1, ParameterTypes: [TypeRef.Any]));
    code.Emit(OpCode.Null); code.Emit(OpCode.MakeRange, 1); code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.MakeClosure, 1, 1);
    code.Emit(OpCode.NativeCall, 2, text: "ranges.any"); code.Emit(OpCode.Pop);
    code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code Barrier()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1); code.Emit(OpCode.Acquire);
    code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code Diamond()
{
    Code code = new(); code.Buffer(10, 20); code.Get(0); code.Emit(OpCode.InitializeLocal, 1);
    code.String("MSYNC_INDEXED_OPT_UNUSED"); code.Emit(OpCode.NativeCall, 1, text: "environment.get"); code.Emit(OpCode.IsNull);
    int otherwise = code.Jump(OpCode.JumpIfFalse); code.Set(1, 99); int end = code.Jump(OpCode.Jump);
    code.Target(otherwise); code.Set(1, 77); code.Target(end);
    code.Emit(OpCode.LoadLocal, 1); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
}
Code Loop()
{
    Code code = new(); code.Buffer(1); code.Number(0); code.Emit(OpCode.InitializeLocal, 1); code.Number(0); code.Emit(OpCode.InitializeLocal, 2);
    int header = code.Count; code.Emit(OpCode.LoadLocal, 1); code.Number(3); code.Emit(OpCode.Less); int exit = code.Jump(OpCode.JumpIfFalse);
    code.Emit(OpCode.LoadLocal, 2); code.Get(0); code.Get(0); code.Emit(OpCode.Add); code.Emit(OpCode.Add); code.Emit(OpCode.StoreLocal, 2);
    code.Emit(OpCode.LoadLocal, 1); code.Number(1); code.Emit(OpCode.Add); code.Emit(OpCode.StoreLocal, 1);
    code.Emit(OpCode.Jump, header); code.Target(exit); code.Emit(OpCode.LoadLocal, 2); code.Emit(OpCode.Return); return code;
}

sealed class Code
{
    internal List<Value> Constants { get; } = [];
    internal List<FunctionCode> Helpers { get; } = [];
    private readonly List<Instruction> instructions = [];
    internal int Count => instructions.Count;
    internal void Emit(OpCode op, int a = 0, int b = 0, string? text = null, int span = 0) =>
        instructions.Add(new(op, a, b, text, new("indexed-memory.msync", span, 1)));
    internal void Number(double value) { int index = Constants.Count; Constants.Add(new NumberValue(value)); Emit(OpCode.Constant, index); }
    internal void String(string value) { int index = Constants.Count; Constants.Add(new StringValue(value)); Emit(OpCode.Constant, index); }
    internal int Constant(Value value) { int index = Constants.Count; Constants.Add(value); return index; }
    internal void Native(string name, int count, int span = 0) => Emit(OpCode.NativeCall, count, text: "storage." + name, span: span);
    internal void Buffer(params double[] values)
    {
        foreach (double value in values) Number(value);
        Emit(OpCode.MakeRange, values.Length); Native("buffer_new", 1); Emit(OpCode.InitializeLocal, 0);
    }
    internal void Get(int index, int span = 0) { Emit(OpCode.LoadLocal, 0); Number(index); Native("buffer_get", 2, span); }
    internal void Size() { Emit(OpCode.LoadLocal, 0); Native("buffer_size", 1); }
    internal void Set(int index, double value) { Emit(OpCode.LoadLocal, 0); Number(index); Number(value); Native("buffer_set", 3); Emit(OpCode.Pop); }
    internal int Jump(OpCode op) { int index = instructions.Count; Emit(op); return index; }
    internal void Target(int position) => instructions[position] = instructions[position] with { A = instructions.Count };
    internal FunctionCode Function() => new("indexed-memory", 0, 4, instructions, TypeRef.Number);
}
