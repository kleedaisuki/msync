using System.Collections;
using System.Reflection;
using Msync.Core;
using Msync.Optimization;
using Module = Msync.Core.Module;

int checks = 0;
Value[] constants = [new NumberValue(7), new NumberValue(2), new NumberValue(3), new FunctionValue(-1, NativeName: "bool.not")];
FunctionCode[] functions =
[
    new("entry", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
    new("current_target", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number),
    new("open_source", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Call, B: -1), new(OpCode.Return)], TypeRef.Number),
    new("arithmetic", 0, 0, [new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Add), new(OpCode.Return)], TypeRef.Number),
    new("open_native", 0, 0, [new(OpCode.Constant, 3), new(OpCode.True), new(OpCode.Call, 1, -2), new(OpCode.Return)], TypeRef.Bool),
    new("memory", 1, 1,
    [
        new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "value"), new(OpCode.Pop),
        new(OpCode.MakeClosure, 1), new(OpCode.Call, B: -1), new(OpCode.Pop),
        new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "value"), new(OpCode.Return)
    ], TypeRef.Any, ParameterTypes: [TypeRef.Scope]),
    new("closed_source", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Call, B: 2), new(OpCode.Return)], TypeRef.Number)
];
SessionDispatchTable[] tables = [new(0, new Dictionary<int, int> { [1] = 1 }), new(1, new Dictionary<int, int>())];
Module input = new(constants, functions) { SessionDispatches = tables };
InstructionEffect required = InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke
    | InstructionEffect.MayFail | InstructionEffect.Diverge;
Check((EffectAnalysis.FunctionEffects(input)[2] & required) == required, "Open source call must retain unknown effects");
Check((EffectAnalysis.FunctionEffects(input)[4] & required) == required, "A currently native callee cannot close an open call's effects");
CheckMemory(input);

foreach (int level in new[] { 2, 3 })
{
    Module optimized = Optimizer.Optimize(input, level);
    Check(ReferenceEquals(optimized.SessionDispatches, tables), $"O{level}: dispatch metadata was detached");
    Check(optimized.Functions.Count == functions.Length, $"O{level}: callable identities were compacted");
    Check(optimized.Functions[2].Instructions.Any(instruction => instruction.Op == OpCode.Call && instruction.B == -1),
        $"O{level}: open source call was resolved to its current target");
    Check(optimized.Functions[4].Instructions.Any(instruction => instruction.Op == OpCode.Call && instruction.B == -2),
        $"O{level}: open native call was folded");
    var arithmetic = optimized.Functions[3].Instructions;
    Check(arithmetic.Count == 2 && arithmetic[0].Op == OpCode.Constant
        && optimized.Constants[arithmetic[0].A] is NumberValue { Data: 5 },
        $"O{level}: unrelated arithmetic no longer optimizes");
    Check(optimized.Functions[6].Instructions.All(instruction => instruction.Op != OpCode.Call),
        $"O{level}: ordinary closed calls no longer inline");
    Check((EffectAnalysis.FunctionEffects(optimized)[2] & required) == required, $"O{level}: open effects lost after optimization");
    CheckMemory(optimized);
}
Console.WriteLine($"PASS {checks} open-session optimizer contracts");

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void CheckMemory(Module module)
{
    MethodInfo analyze = typeof(Optimizer).Assembly.GetType("Msync.Optimization.MemoryEffects")!
        .GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!;
    object summary = ((IEnumerable)analyze.Invoke(null, [module])!).Cast<object>().ElementAt(5);
    bool Flag(string name) => (bool)summary.GetType().GetProperty(name)!.GetValue(summary)!;
    Check(Flag("UnknownReads") && Flag("UnknownWrites"), "An open call must invalidate caller-visible memory");
    Check(Flag("UnknownReturnAlias"), "An open call cannot prove a fresh or alias-free result");
}
