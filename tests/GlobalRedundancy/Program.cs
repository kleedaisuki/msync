using System.Reflection;
using System.Diagnostics;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

var pass = typeof(Optimizer).Assembly.GetType("Msync.Core.GlobalRedundancy")!.GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
var assertions = 0;
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "global-redundancy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--performance", StringComparer.Ordinal)) { Performance(); return; }
    await FullRedundancy();
    await PartialRedundancy(takenEdge: false);
    await PartialRedundancy(takenEdge: true);
    await Mutation();
    await CapturedMutation();
    await LoopVersions();
    await SelectedFailure();
    await SignedZero();
    Console.WriteLine($"Global redundancy: {assertions} contracts passed.");
}
finally { Directory.Delete(root, recursive: true); }

void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

FunctionCode Optimize(FunctionCode function, Value[] constants, FunctionCode[]? others = null)
{
    Module module = new(constants, [function, .. others ?? []], OptimizationLevel: 0);
    return (FunctionCode)pass.Invoke(null, [module, function, constants.ToList()])!;
}

FunctionCode Product(FunctionCode function, int level) =>
    Optimizer.Optimize(new([], [function], OptimizationLevel: 0), level).Functions[0];

Module Invoke(FunctionCode function, Value[] arguments, FunctionCode[]? others = null, Value[]? extraConstants = null)
{
    var constants = arguments.Concat(extraConstants ?? []).ToArray();
    List<Instruction> entry = [new(OpCode.MakeClosure, 1)];
    for (var index = 0; index < arguments.Length; index++) entry.Add(new(OpCode.Constant, index));
    entry.Add(new(OpCode.Call, arguments.Length, 2)); entry.Add(new(OpCode.Return));
    return new(constants, [new("main", 0, 0, entry, function.ReturnType), function, .. others ?? []],
        Stage: ArtifactStage.Executable, Context: new(root, root), OptimizationLevel: 0);
}

async Task<string> Outcome(Module module)
{
    try
    {
        var result = await Interpreter.ExecuteAsync(module);
        return result switch
        {
            RangeValue range => "Range:" + string.Join("|", await Materialize(range)),
            _ => result.Type + ":" + result
        };
    }
    catch (MsyncException error) { return "error:" + error.Message + ":" + error.Span; }
}

async Task<List<string>> Materialize(RangeValue range)
{
    var values = new List<string>();
    await foreach (var item in range.Items) values.Add(item.Type + ":" + item);
    return values;
}

async Task Check(FunctionCode baseline, FunctionCode changed, Value[] arguments, string expected, FunctionCode[]? others = null, Value[]? extra = null)
{
    var before = Invoke(baseline, arguments, others, extra); var after = Invoke(changed, arguments, others, extra);
    Artifact.Validate(before); Artifact.Validate(after);
    Require(await Outcome(before) == expected, baseline.Name + ": independent baseline result");
    Require(await Outcome(after) == expected, baseline.Name + ": changed result");
    using MemoryStream stream = new(); Artifact.Write(after, stream); stream.Position = 0;
    Require(await Outcome(Artifact.Read(stream)) == expected, baseline.Name + ": serialized result");
}

async Task FullRedundancy()
{
    FunctionCode function = new("dominating-number-result", 3, 5,
    [new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.Multiply), new(OpCode.InitializeLocal, 3),
     new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 10), new(OpCode.LoadLocal, 3), new(OpCode.Negate), new(OpCode.InitializeLocal, 4), new(OpCode.Jump, 12),
     new(OpCode.LoadLocal, 3), new(OpCode.InitializeLocal, 4),
     new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.Multiply), new(OpCode.MakeRange, 2), new(OpCode.Return)],
     new(TypeKind.Range, TypeRef.Number), ParameterTypes: [TypeRef.Bool, TypeRef.Number, TypeRef.Number]);
    var changed = Optimize(function, []);
    Require(changed.Instructions.Count(i => i.Op == OpCode.Multiply) == 1, "Dominator FRE did not reuse the available result local");
    var ordinary = Product(function, 1);
    Require(ordinary.Instructions.Count(i => i.Op == OpCode.Multiply) == 2, "FRE discriminator unexpectedly disappeared before the global pass");
    foreach (var candidate in new[] { changed, Product(function, 2), Product(function, 3) })
    {
        Require(candidate.Instructions.Count(i => i.Op == OpCode.Multiply) == 1, "Product optimizer did not consume global FRE");
        foreach (var condition in new[] { false, true })
            await Check(function, candidate, [new BoolValue(condition), new NumberValue(7), new NumberValue(9)],
                condition ? "Range:Number:-63|Number:63" : "Range:Number:63|Number:63");
    }
}

async Task PartialRedundancy(bool takenEdge)
{
    // The second branch's edge to the join is critical: its other successor
    // returns immediately. PRE must not initialize the result on that path.
    var branch = takenEdge ? OpCode.JumpIfTrue : OpCode.JumpIfFalse;
    FunctionCode function = new("partial-string-" + takenEdge, 4, 5,
    [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 7),
     new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3), new(OpCode.Add), new(OpCode.InitializeLocal, 4), new(OpCode.Jump, 15),
     new(OpCode.LoadLocal, 2), new(OpCode.InitializeLocal, 4), new(OpCode.LoadLocal, 1), new(branch, 15),
     new(OpCode.LoadLocal, 2), new(OpCode.MakeRange, 1), new(OpCode.Return), new(OpCode.Nop),
     new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3), new(OpCode.Add), new(OpCode.MakeRange, 2), new(OpCode.Return)],
     new(TypeKind.Range, TypeRef.String), ParameterTypes: [TypeRef.Bool, TypeRef.Bool, TypeRef.String, TypeRef.String]);
    var changed = Optimize(function, []);
    Require(changed.LocalCount == function.LocalCount + 1, "PRE did not allocate its shared result location");
    Require(changed.Instructions.Count(i => i.Op == OpCode.Add) == 2, "PRE did not replace the join computation and split the missing edge");
    Require(changed.Instructions[^1].Op == OpCode.Jump, "Critical taken edge did not produce an explicit split block");
    var ordinary = Product(function, 1);
    Require(CountAdds(ordinary, true, true) == 2, "PRE discriminator vanished before global optimization");
    foreach (var candidate in new[] { changed, Product(function, 2), Product(function, 3) })
    {
        Require(CountAdds(candidate, true, true) == 1, "Product optimizer did not eliminate the partially redundant join computation");
        foreach (var selected in new[] { false, true })
            foreach (var other in new[] { false, true })
            {
                var joined = selected || other == takenEdge;
                await Check(function, candidate, [new BoolValue(selected), new BoolValue(other), new StringValue("Klee"), new StringValue("世界")], joined
                    ? selected ? "Range:String:Klee世界|String:Klee世界" : "Range:String:Klee|String:Klee世界" : "Range:String:Klee");
            }
    }
}

int CountAdds(FunctionCode function, bool first, bool second)
{
    var locals = new Value[function.LocalCount]; Array.Fill(locals, Value.Null);
    locals[0] = new BoolValue(first); locals[1] = new BoolValue(second); locals[2] = new StringValue("Klee"); locals[3] = new StringValue("世界");
    List<Value> stack = []; var count = 0;
    Value Pop() { var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item; }
    for (var pc = 0; pc < function.Instructions.Count; pc++)
    {
        var instruction = function.Instructions[pc];
        switch (instruction.Op)
        {
            case OpCode.Nop: break;
            case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
            case OpCode.StoreLocal: case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
            case OpCode.Dup: var copy = Pop(); stack.Add(copy); stack.Add(copy); break;
            case OpCode.Pop: Pop(); break;
            case OpCode.Jump: pc = instruction.A - 1; break;
            case OpCode.JumpIfFalse:
            case OpCode.JumpIfTrue:
                if (((BoolValue)Pop()).Data == (instruction.Op == OpCode.JumpIfTrue)) pc = instruction.A - 1;
                break;
            case OpCode.Add: var right = (StringValue)Pop(); var left = (StringValue)Pop(); stack.Add(new StringValue(left.Data + right.Data)); count++; break;
            case OpCode.MakeRange:
                for (var item = 0; item < instruction.A; item++) Pop(); stack.Add(Value.Null); break;
            case OpCode.Return: return count;
            default: throw new InvalidOperationException("Unexpected PRE trace instruction: " + instruction.Op);
        }
    }
    return count;
}

async Task Mutation()
{
    FunctionCode function = new("reaching-store-version", 3, 4,
    [new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.Multiply), new(OpCode.InitializeLocal, 3),
     new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 8), new(OpCode.LoadLocal, 2), new(OpCode.StoreLocal, 1),
     new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.Multiply), new(OpCode.Return)],
     TypeRef.Number, ParameterTypes: [TypeRef.Bool, TypeRef.Number, TypeRef.Number]);
    var changed = Optimize(function, []);
    foreach (var condition in new[] { false, true })
        await Check(function, changed, [new BoolValue(condition), new NumberValue(7), new NumberValue(9)], condition ? "Number:81" : "Number:63");
    Require(changed.Instructions.Count(i => i.Op == OpCode.Multiply) == 2, "A store-merged operand incorrectly reused a stale expression");
}

async Task CapturedMutation()
{
    FunctionCode function = new("captured-alias-version", 1, 4,
    [new(OpCode.LoadLocal, 0), new(OpCode.Not), new(OpCode.InitializeLocal, 1),
     new(OpCode.LoadCell, 0), new(OpCode.InitializeLocal, 2), new(OpCode.False), new(OpCode.StoreLocal, 2),
     new(OpCode.LoadLocal, 0), new(OpCode.Not), new(OpCode.Return)],
     TypeRef.Bool, ParameterTypes: [TypeRef.Bool]);
    var changed = Optimize(function, []);
    await Check(function, changed, [Value.True], "Bool:true");
    Require(changed.Instructions.Count(i => i.Op == OpCode.Not) == 2, "Raw cell alias write reused the source's old value");
}

async Task LoopVersions()
{
    FunctionCode function = new("loop-dynamic-version", 1, 3,
    [new(OpCode.LoadLocal, 0), new(OpCode.Negate), new(OpCode.InitializeLocal, 1),
     new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Greater), new(OpCode.JumpIfFalse, 16),
     new(OpCode.LoadLocal, 0), new(OpCode.Negate), new(OpCode.InitializeLocal, 2),
     new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.Subtract), new(OpCode.StoreLocal, 0), new(OpCode.Jump, 3), new(OpCode.Nop),
     new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number, ParameterTypes: [TypeRef.Number]);
    var constants = new Value[] { new NumberValue(3), new NumberValue(0), new NumberValue(1) };
    var changed = Optimize(function, constants);
    await Check(function, changed, [constants[0]], "Number:0", extra: [constants[1], constants[2]]);
}

async Task SelectedFailure()
{
    FunctionCode function = new("selected-error", 1, 1,
    [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 7), new(OpCode.Constant, 1), new(OpCode.RegexMatch, Text: "["), new(OpCode.Pop), new(OpCode.True), new(OpCode.Return), new(OpCode.False), new(OpCode.Return)],
     TypeRef.Bool, ParameterTypes: [TypeRef.Bool]);
    var changed = Optimize(function, [Value.True, new StringValue("Klee")]);
    await Check(function, changed, [Value.False], "Bool:false", extra: [new StringValue("Klee")]);
    var before = Invoke(function, [Value.True], extraConstants: [new StringValue("Klee")]);
    var after = Invoke(changed, [Value.True], extraConstants: [new StringValue("Klee")]);
    Require(await Outcome(before) == await Outcome(after), "Selected regex diagnostic was changed by scalar PRE");
}

async Task SignedZero()
{
    FunctionCode function = new("bit-exact-number-identity", 0, 1,
    [new(OpCode.Constant, 0), new(OpCode.Negate), new(OpCode.InitializeLocal, 0), new(OpCode.Jump, 4),
     new(OpCode.Constant, 1), new(OpCode.Negate), new(OpCode.Return)], TypeRef.Number);
    var constants = new Value[] { new NumberValue(0.0), new NumberValue(-0.0) };
    var changed = Optimize(function, constants);
    Require(changed.Instructions.Count(i => i.Op == OpCode.Negate) == 2, "Numeric semantic equality collapsed distinct constant representations");
    foreach (var candidate in new[] { function, changed })
    {
        var value = await Interpreter.ExecuteAsync(Invoke(candidate, [], extraConstants: constants));
        Require(value is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == 0, "Signed zero result was replaced by the opposite representation");
    }
}

void Performance()
{
    foreach (var size in new[] { 100, 500, 2000 })
        foreach (var branching in new[] { false, true })
        {
            var constants = Enumerable.Range(0, size).Select(index => (Value)new NumberValue(index)).ToArray();
            List<Instruction> instructions = [];
            for (var index = 0; index < size; index++)
            {
                instructions.Add(new(OpCode.LoadLocal, 0)); instructions.Add(new(OpCode.Constant, index));
                instructions.Add(new(OpCode.Add)); instructions.Add(new(OpCode.InitializeLocal, index + 2));
                if (branching && index == size / 2)
                {
                    instructions.Add(new(OpCode.LoadLocal, 1));
                    instructions.Add(new(OpCode.JumpIfFalse, instructions.Count + 2));
                    instructions.Add(new(OpCode.Nop));
                }
            }
            instructions.Add(new(OpCode.LoadLocal, size + 1)); instructions.Add(new(OpCode.Return));
            FunctionCode function = new("scaling", 2, size + 2, instructions, TypeRef.Number, ParameterTypes: [TypeRef.Number, TypeRef.Bool]);
            for (var warmup = 0; warmup < 3; warmup++) Optimize(function, constants);
            List<double> milliseconds = []; List<long> allocations = [];
            for (var sample = 0; sample < 9; sample++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                var optimized = Optimize(function, constants);
                milliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                Require(ReferenceEquals(function, optimized), "No redundant expression should allocate runtime result storage");
            }
            milliseconds.Sort(); allocations.Sort();
            Console.WriteLine($"Global redundancy n={size}, branch={branching}: median {milliseconds[4]:F3} ms, {allocations[4]} allocated bytes");
        }
}
