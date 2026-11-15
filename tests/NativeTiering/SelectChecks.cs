using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class SelectChecks
{
    internal static async Task<int> RunAsync()
    {
        if ((int)NativeOp.MakeType != 45 || (int)NativeOp.Select != 46 || (int)NativeBinary.Qualify != 14)
            throw new InvalidOperationException("Appending Select and Bool operators changed an existing Native2 wire number.");
        NativeProgram original = BuildProgram();
        using MemoryStream artifact = new();
        NativeArtifact.Write(original, artifact); artifact.Position = 0;
        int checks = 0;
        foreach (NativeProgram program in new[] { original, NativeArtifact.Read(artifact) })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
            foreach (bool left in new[] { false, true })
                foreach (bool right in new[] { false, true })
                    foreach (string name in new[] { "BoolAnd", "BoolOr", "BoolXor", "SMin", "SMax", "UMin", "UMax", "SelectBool" })
                    {
                        bool expected = name switch
                        {
                            "BoolAnd" or "SMax" or "UMin" or "SelectBool" => left & right,
                            "BoolXor" => left ^ right,
                            _ => left | right
                        };
                        NativeValue result = await interpreter.InvokeAsync(Callable(program, name), [NativeValue.FromBool(left), NativeValue.FromBool(right)]);
                        Require(result.AsBool() == expected, $"{name}({left}, {right}) must be {expected}."); ++checks;
                    }
            foreach (bool condition in new[] { false, true })
            {
                NativeValue result = await interpreter.InvokeAsync(Callable(program, "SelectNumber"),
                    [NativeValue.FromBool(condition), NativeValue.FromNumber(7), NativeValue.FromNumber(9)]);
                Require(result.AsNumber() == (condition ? 7 : 9), "Typed Number select lost the chosen payload."); ++checks;
            }
        }
        checks += await PendingAsync();
        checks += await RetainedProcessAsync();
        return checks;
    }

    private static NativeValue Callable(NativeProgram program, string name) => NativeValue.FromFunction(new(
        Enumerable.Range(0, program.Functions.Count).Single(index => program.Functions[index].Name == name)));

    private static NativeProgram BuildProgram()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "native-selection.msync"));
        var types = input.Types.ToList();
        int number = types.FindIndex(type => type.Kind == TypeKind.Number && type.Pointee < 0);
        int boolean = types.FindIndex(type => type.Kind == TypeKind.Bool && type.Pointee < 0);
        if (boolean < 0) { boolean = types.Count; types.Add(new(TypeKind.Bool)); }
        var functions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value);
        LlvmType boolType = LlvmType.GetInt1Ty(input.Module.Context);
        foreach (string name in new[] { "BoolAnd", "BoolOr", "BoolXor", "SMin", "SMax", "UMin", "UMax", "SelectBool" })
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(boolType, [boolType, boolType]),
                LlvmLinkage.InternalLinkage, name, input.Module);
            foreach (var argument in function.Arguments) argument.LogicalType = boolean;
            LlvmValue left = function.Arguments[0], right = function.Arguments[1];
            LlvmInstruction operation = name switch
            {
                "BoolAnd" => new LlvmBinaryOperator(LlvmOpcode.And, left, right),
                "BoolOr" => new LlvmBinaryOperator(LlvmOpcode.Or, left, right),
                "BoolXor" => new LlvmBinaryOperator(LlvmOpcode.Xor, left, right),
                "SelectBool" => new LlvmSelectInstruction(left, right, LlvmConstantInt.GetFalse(input.Module.Context)),
                _ => LlvmIntrinsic.CreateBinary(name switch
                {
                    "SMin" => LlvmIntrinsicKind.SMin, "SMax" => LlvmIntrinsicKind.SMax,
                    "UMin" => LlvmIntrinsicKind.UMin, _ => LlvmIntrinsicKind.UMax
                }, left, right, input.Module)
            };
            operation.LogicalType = boolean;
            LlvmBasicBlock body = new(input.Module.Context, "entry", function);
            body.Append(operation); body.Append(new LlvmReturnInstruction(input.Module.Context, operation));
            functions.Add(function, new(0, boolean));
        }
        LlvmType doubleType = LlvmType.GetDoubleTy(input.Module.Context);
        var select = LlvmFunction.Create(LlvmFunctionType.Get(doubleType, [boolType, doubleType, doubleType]),
            LlvmLinkage.InternalLinkage, "SelectNumber", input.Module);
        select.Arguments[0].LogicalType = boolean;
        select.Arguments[1].LogicalType = select.Arguments[2].LogicalType = number;
        LlvmSelectInstruction selected = new(select.Arguments[0], select.Arguments[1], select.Arguments[2]) { LogicalType = number };
        LlvmBasicBlock entry = new(input.Module.Context, "entry", select);
        entry.Append(selected); entry.Append(new LlvmReturnInstruction(input.Module.Context, selected));
        functions.Add(select, new(0, number));
        return LlvmNativeLowerer.Lower(input with { Types = types, SourceFunctions = functions });
    }

    private static async Task<int> PendingAsync()
    {
        NativeType physical = NativeType.Of(NativeKind.Number);
        TaskCompletionSource<NativeValue> condition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NativeValue> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum unselected = new(new NativeFuture(never.Task, logicalType: TypeRef.Number));
        NativeDatum selected = NativeDatum.Select(new(new NativeFuture(condition.Task, logicalType: TypeRef.Bool)),
            new(NativeValue.FromNumber(7)), unselected, null, TypeRef.Number, physical);
        Require(!selected.IsReady && !selected.Future!.Completion.IsCompleted, "A pending condition must remain compositional.");
        condition.SetResult(NativeValue.True);
        Require((await selected.ResolveAsync(CancellationToken.None)).AsNumber() == 7 && !never.Task.IsCompleted,
            "Select must not await the unselected pending arm.");
        NativeDatum failed = new(new NativeFuture(Task.FromException<NativeValue>(new InvalidOperationException("unselected")), logicalType: TypeRef.Number));
        NativeDatum ready = NativeDatum.Select(new(NativeValue.False), failed, new(NativeValue.FromNumber(9)), null, TypeRef.Number, physical);
        Require(ready.IsReady && ready.Value.AsNumber() == 9, "A ready condition must return the chosen datum without demanding a failed arm.");
        return 3;
    }

    private static async Task<int> RetainedProcessAsync()
    {
        string work = Path.GetFullPath(Path.Combine(".temp", "native-selection")); Directory.CreateDirectory(work);
        NativeRuntimeContext context = new(new(WorkingDirectory: work));
        NativeProcess process = await NativeHostIO.StartProcessAsync(new("/bin/sh"),
            [NativeValue.FromString("-c"), NativeValue.FromString("read value; printf 7")], context, true, CancellationToken.None);
        var parent = new NativeWorkScope(null);
        var child = new NativeWorkScope(parent);
        TaskCompletionSource<NativeValue> condition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NativeValue> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeFuture decision = new(condition.Task, logicalType: TypeRef.Bool);
        NativeFuture good = new(ProcessNumberAsync(process), processes: [process], logicalType: TypeRef.Number);
        NativeFuture bad = new(never.Task, logicalType: TypeRef.Number);
        child.Add(process); child.Add(decision); child.Add(good); child.Add(bad);
        NativeDatum result = NativeDatum.Select(new(decision), new(good), new(bad), null, TypeRef.Number, NativeType.Of(NativeKind.Number));
        child.Add(result.Future!);
        NativeReachable reachable = new(new NativeMemory(), _ => null); reachable.Add(result);
        await child.FinishAsync(reachable, static () => { }, CancellationToken.None);
        Require(parent.Pending.Contains(good) && reachable.Processes.Contains(process) && process.Control!.IsRunning,
            "Returning a pending selection must promote candidate process resources rather than closing them with the submitting scope.");
        condition.SetResult(NativeValue.True);
        await process.Control!.WriteInputAsync("go\n", CancellationToken.None);
        await process.Control.CloseInputAsync(CancellationToken.None);
        Require((await result.ResolveAsync(CancellationToken.None)).AsNumber() == 7 && !never.Task.IsCompleted,
            "The process-derived selected arm must survive scope exit without demanding its unselected pending peer.");
        await parent.FinishAsync(new(new NativeMemory(), _ => null), static () => { }, CancellationToken.None);
        return 2;
    }

    private static async Task<NativeValue> ProcessNumberAsync(NativeProcess process)
    {
        NativeProcess complete = await process.WaitAsync(CancellationToken.None);
        return NativeValue.FromNumber(double.Parse(complete.StandardOutput, CultureInfo.InvariantCulture));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
