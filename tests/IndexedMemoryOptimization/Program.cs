using Msync.Optimization;
using System.Reflection;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--measure", StringComparer.Ordinal))
{
    await Measurements.Run();
    return;
}

MethodInfo pass = typeof(Optimizer).Assembly.GetType("Msync.Optimization.IndexedMemoryOptimization")!
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

foreach (var item in new[]
{
    (Name: "edge-copy", Program: SelectedEdge(), Gets: 2, Left: 10d, Right: 20d),
    (Name: "edge-expression", Program: SelectedExpression(), Gets: 2, Left: 20d, Right: 30d),
    (Name: "edge-local", Program: SelectedLocal(false, false), Gets: 2, Left: 20d, Right: 40d),
    (Name: "edge-alias-write", Program: SelectedLocal(true, false), Gets: 3, Left: 109d, Right: 40d),
    (Name: "edge-callback", Program: SelectedLocal(false, true), Gets: 3, Left: 109d, Right: 40d),
    (Name: "edge-receiver", Program: SelectedReceiver(), Gets: 2, Left: 10d, Right: 20d),
    (Name: "edge-mixed-cell", Program: MixedCell(), Gets: 2, Left: 40d, Right: 30d),
})
{
    var program = item.Program;
    var original = program.Function();
    var rewritten = Rewrite(original, program.Constants);
    Check(GetCount(rewritten) == item.Gets, item.Name + " edge pass residual gets");
    Check(Rewrite(rewritten, program.Constants).Instructions.SequenceEqual(rewritten.Instructions),
        item.Name + " edge pass idempotence");
    if (item.Name == "edge-local")
        Check(rewritten.LocalCount == original.LocalCount, "reuse the existing selected-result local");
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module raw = new(program.Constants, [original, .. program.Helpers], OptimizationLevel: 0);
        Module optimized = Optimizer.Optimize(raw, level);
        if (level >= 2)
            Check(optimized.Functions.Sum(GetCount) == item.Gets, item.Name + " public residual gets");
        foreach (Module image in Images(optimized))
        foreach (bool left in new[] { true, false })
        {
            var options = new ExecutionOptions { Input = new StringReader(left ? "" : "right\n") };
            Value result = await Interpreter.ExecuteAsync(image, options);
            Check(result is NumberValue number && number.Data == (left ? item.Left : item.Right),
                $"{item.Name} O{level}, left={left}: {result}");
        }
    }
}

foreach (bool left in new[] { true, false })
{
    Code code = new(); code.Buffer(10);
    code.InputIsNull(); int other = code.Jump(OpCode.JumpIfFalse);
    code.Number(3); code.Emit(OpCode.InitializeLocal, 1);
    code.GetLocal(1, 71); code.Emit(OpCode.Pop); int join = code.Jump(OpCode.Jump);
    code.Target(other); code.Number(4); code.Emit(OpCode.InitializeLocal, 1);
    code.GetLocal(1, 81); code.Emit(OpCode.Pop);
    code.Target(join); code.GetLocal(1, 91); code.Emit(OpCode.Return);
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(new(code.Constants, [code.Function()], OptimizationLevel: 0), level);
        if (level >= 2) Check(optimized.Functions.Sum(GetCount) == 2, "edge failures retained, selected reread removed");
        foreach (Module image in Images(optimized))
        {
            var options = new ExecutionOptions { Input = new StringReader(left ? "" : "right\n") };
            try { await Interpreter.ExecuteAsync(image, options); throw new InvalidOperationException("Missing edge bounds failure"); }
            catch (MsyncException error)
            {
                Check(error.Span?.Start == (left ? 71 : 81), $"O{level}: wrong first failing edge span: {error.Span}");
            }
        }
    }
}

foreach (var (name, operation, index, other, capture, expected, gets) in new[]
{
    ("callee-read", "buffer_size", 0, false, false, 20d, 1),
    ("callee-disjoint-index", "buffer_set", 1, false, false, 20d, 1),
    ("callee-overlap", "buffer_set", 0, false, false, 109d, 2),
    ("callee-fresh-parameter", "buffer_push", 0, true, false, 20d, 1),
    ("callee-fresh-capture", "buffer_push", 0, true, true, 20d, 1),
    ("callee-capture-overlap", "buffer_set", 0, false, true, 109d, 2),
})
{
    Code program = new();
    if (other)
    {
        program.Buffer(1, 2);
        program.Emit(OpCode.LoadLocal, 0);
        program.Emit(OpCode.InitializeLocal, 2);
    }
    program.Buffer(10, 20);
    program.Get(0);
    program.Emit(OpCode.InitializeLocal, 1);
    if (capture) program.Emit(OpCode.LoadLocal, other ? 2 : 0);
    program.Emit(OpCode.MakeClosure, 1, capture ? 1 : 0);
    if (!capture) program.Emit(OpCode.LoadLocal, other ? 2 : 0);
    program.Emit(OpCode.Call, capture ? 0 : 1, 2);
    program.Emit(OpCode.Pop);
    program.Emit(OpCode.LoadLocal, 1);
    program.Get(0);
    program.Emit(OpCode.Add);
    program.Emit(OpCode.Return);

    List<Instruction> helper = [];
    // Keep the read-only case at an actual call boundary: unlike a direct
    // Buffer observation, an external input operation is not inlined.
    if (operation == "buffer_size")
        helper.AddRange([new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Pop)]);
    int arguments = helper.Count;
    helper.Add(new(OpCode.LoadLocal, 0));
    if (operation == "buffer_set") helper.Add(new(OpCode.Constant, program.Constant(new NumberValue(index))));
    if (operation is "buffer_set" or "buffer_push") helper.Add(new(OpCode.Constant, program.Constant(new NumberValue(99))));
    helper.Add(new(OpCode.NativeCall, helper.Count - arguments, Text: "storage." + operation));
    helper.Add(new(OpCode.Return));
    var function = new FunctionCode(name, capture ? 0 : 1, 1, helper,
        operation == "buffer_set" ? TypeRef.Null : TypeRef.Number, capture ? 1 : 0,
        capture ? [] : [new(TypeKind.Range)]);
    Module raw = new(program.Constants, [program.Function(), function], OptimizationLevel: 0);
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(raw, level);
        Check(optimized.Functions[optimized.EntryPoint].Instructions.Any(instruction => instruction.Op == OpCode.Call),
            name + " must retain the actual call boundary");
        Check(optimized.Functions.Sum(GetCount) == (level == 0 ? 2 : gets), name + " resource-relative residual gets");
        foreach (Module image in Images(optimized))
            Check(await Interpreter.ExecuteAsync(image) is NumberValue number && number.Data == expected, $"{name} O{level}");
    }
}

{
    // A wrapper binds two formal arguments to one locally allocated Scope.
    // The callee replaces its Buffer through one name, then writes through
    // the other. Projecting its indexed path onto the entry field would see
    // only the old local Buffer and lose the write to the caller's argument.
    Code program = new();
    program.Buffer(10, 20);
    program.Get(0);
    program.Emit(OpCode.InitializeLocal, 1);
    program.Emit(OpCode.MakeClosure, 1);
    program.Emit(OpCode.LoadLocal, 0);
    program.Emit(OpCode.Call, 1, 2);
    program.Emit(OpCode.Pop);
    program.Emit(OpCode.LoadLocal, 1);
    program.Get(0);
    program.Emit(OpCode.Add);
    program.Emit(OpCode.Return);
    int zero = program.Constant(new NumberValue(0));
    int replacement = program.Constant(new NumberValue(99));
    TypeRef range = new(TypeKind.Range);
    var wrapper = new FunctionCode("alias-wrapper", 1, 2,
        [
            new(OpCode.Constant, zero), new(OpCode.MakeRange, 1), new(OpCode.NativeCall, 1, Text: "storage.buffer_new"),
            new(OpCode.MakeScope, 1, Text: "items"), new(OpCode.InitializeLocal, 1),
            new(OpCode.MakeClosure, 2), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 1),
            new(OpCode.LoadLocal, 0), new(OpCode.Call, 3, 3), new(OpCode.Return)
        ], TypeRef.Null, ParameterTypes: [range]);
    var mutate = new FunctionCode("alias-rebind-and-write", 3, 3,
        [
            new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2), new(OpCode.SetMember, Text: "items"), new(OpCode.Pop),
            new(OpCode.LoadLocal, 1), new(OpCode.GetMember, Text: "items"), new(OpCode.Constant, zero),
            new(OpCode.Constant, replacement), new(OpCode.NativeCall, 3, Text: "storage.buffer_set"), new(OpCode.Return)
        ], TypeRef.Null, ParameterTypes: [TypeRef.Scope, TypeRef.Scope, range]);
    Module raw = new(program.Constants, [program.Function(), wrapper, mutate], OptimizationLevel: 0);
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(raw, level);
        Check(optimized.Functions.Sum(GetCount) == 2, "transitive aliased receiver rebinding must invalidate contents");
        foreach (Module image in Images(optimized))
            Check(await Interpreter.ExecuteAsync(image) is NumberValue { Data: 109 }, $"transitive alias rebinding O{level}");
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
int GetCount(FunctionCode function) => function.Instructions.Count(instruction =>
    instruction is { Op: OpCode.NativeCall, Text: "storage.buffer_get" });
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

Code SelectedEdge()
{
    Code code = new(); code.Buffer(10, 20);
    code.Get(0); code.Emit(OpCode.Pop); code.Get(1); code.Emit(OpCode.Pop);
    code.Number(0); code.Emit(OpCode.InitializeLocal, 1);
    code.InputIsNull(); int join = code.Jump(OpCode.JumpIfTrue);
    code.Number(1); code.Emit(OpCode.StoreLocal, 1);
    code.Target(join); code.GetLocal(1); code.Emit(OpCode.Return); return code;
}

Code SelectedExpression()
{
    Code code = new(); code.Buffer(10, 20, 30);
    code.InputIsNull(); int other = code.Jump(OpCode.JumpIfFalse);
    code.Number(0); code.Emit(OpCode.InitializeLocal, 1); Read(); code.Emit(OpCode.Pop);
    int join = code.Jump(OpCode.Jump);
    code.Target(other); code.Number(1); code.Emit(OpCode.InitializeLocal, 1); Read(); code.Emit(OpCode.Pop);
    code.Target(join); Read(); code.Emit(OpCode.Return); return code;

    void Read()
    {
        code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.LoadLocal, 1); code.Number(1);
        code.Emit(OpCode.Add); code.Native("buffer_get", 2);
    }
}

Code SelectedLocal(bool write, bool callback)
{
    Code code = new(); code.Buffer(10, 20);
    code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 3);
    code.InputIsNull(); int other = code.Jump(OpCode.JumpIfFalse);
    Read(0);
    if (write)
    {
        code.Emit(OpCode.LoadLocal, 3); code.Number(0); code.Number(99);
        code.Native("buffer_set", 3); code.Emit(OpCode.Pop);
    }
    if (callback)
    {
        int zero = code.Constant(new NumberValue(0)), changed = code.Constant(new NumberValue(99));
        code.Helpers.Add(new("mutate", 0, 1,
        [
            new(OpCode.LoadLocal, 0), new(OpCode.Constant, zero), new(OpCode.Constant, changed),
            new(OpCode.NativeCall, 3, Text: "storage.buffer_set"), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)
        ], TypeRef.Null, CaptureCount: 1));
        code.Emit(OpCode.LoadLocal, 3); code.Emit(OpCode.MakeClosure, 1, 1);
        code.Emit(OpCode.Call, 0); code.Emit(OpCode.Pop);
    }
    int join = code.Jump(OpCode.Jump);
    code.Target(other); Read(1);
    code.Target(join); code.Emit(OpCode.LoadLocal, 2);
    code.GetLocal(1); code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;

    void Read(int index)
    {
        code.Number(index); code.Emit(OpCode.InitializeLocal, 1);
        code.GetLocal(1); code.Emit(OpCode.InitializeLocal, 2);
    }
}

Code SelectedReceiver()
{
    Code code = new(); code.Buffer(10);
    code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 1); code.Buffer(20);
    code.InputIsNull(); int other = code.Jump(OpCode.JumpIfFalse);
    code.Emit(OpCode.LoadLocal, 1); Read(true); int join = code.Jump(OpCode.Jump);
    code.Target(other); code.Emit(OpCode.LoadLocal, 0); Read(true);
    code.Target(join); code.Emit(OpCode.LoadLocal, 2); Read(false); code.Emit(OpCode.Return); return code;

    void Read(bool remember)
    {
        if (remember) { code.Emit(OpCode.Dup); code.Emit(OpCode.InitializeLocal, 2); }
        code.Number(0); code.Native("buffer_get", 2);
        if (remember) code.Emit(OpCode.Pop);
    }
}

Code MixedCell()
{
    Code code = new(); code.Buffer(20);
    code.Emit(OpCode.LoadLocal, 0); code.Emit(OpCode.InitializeLocal, 1); code.Buffer(10);
    code.InputIsNull(); int join = code.Jump(OpCode.JumpIfFalse);
    code.Emit(OpCode.LoadCell, 0); code.Native("buffer_size", 1); code.Emit(OpCode.Pop);
    code.Target(join); code.Emit(OpCode.LoadStorage, 0); code.Emit(OpCode.InitializeLocal, 2);
    code.Emit(OpCode.LoadLocal, 1); code.Emit(OpCode.StoreLocal, 0);
    code.Get(0); code.Emit(OpCode.LoadLocal, 2); code.Number(0); code.Native("buffer_get", 2);
    code.Emit(OpCode.Add); code.Emit(OpCode.Return); return code;
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
    internal void GetLocal(int slot, int span = 0) { Emit(OpCode.LoadLocal, 0); Emit(OpCode.LoadLocal, slot); Native("buffer_get", 2, span); }
    internal void InputIsNull() { Emit(OpCode.NativeCall, 0, text: "io.read_line"); Emit(OpCode.IsNull); }
    internal void Size() { Emit(OpCode.LoadLocal, 0); Native("buffer_size", 1); }
    internal void Set(int index, double value) { Emit(OpCode.LoadLocal, 0); Number(index); Number(value); Native("buffer_set", 3); Emit(OpCode.Pop); }
    internal int Jump(OpCode op) { int index = instructions.Count; Emit(op); return index; }
    internal void Target(int position) => instructions[position] = instructions[position] with { A = instructions.Count };
    internal FunctionCode Function() => new("indexed-memory", 0, 4, instructions, TypeRef.Number);
}
