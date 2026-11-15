using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime.Native;

internal static class CloningChecks
{
    internal static async Task<int> RunAsync()
    {
        Remapping();
        OperationIdentity();
        OperandReplacement();
        await ExecuteCloneAsync();
        return 14;
    }

    private static void Remapping()
    {
        LlvmContext context = new();
        var number = LlvmType.GetDoubleTy(context);
        var module = new LlvmModule("clone-remapping", context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(number, [number]), LlvmLinkage.InternalLinkage, "original", module);
        LlvmBasicBlock source = new(context, "body", function), other = new(context, "other", function);
        var phi = new LlvmPhiInstruction(number) { LogicalType = 3, Source = new("clone.msync", 4, 2) };
        phi.AddIncoming(function.Arguments[0], other); phi.GetOperandUse(0).LogicalType = 7;
        source.Append(phi); source.Append(new LlvmReturnInstruction(context, phi));
        other.Append(new LlvmUncondBrInstruction(source));
        Dictionary<LlvmValue, LlvmValue> map = [];
        var clone = LlvmCloning.CloneBasicBlock(source, map, ".copy", function);
        Program.Require(!map.ContainsKey(source) && clone.Name == "body.copy", "CloneBasicBlock records instructions, not an implicit block mapping.");
        var clonedPhi = (LlvmPhiInstruction)clone.FirstInstruction!;
        Program.Require(clonedPhi.Source == phi.Source && clonedPhi.LogicalType == 3 && clonedPhi.GetOperandUse(0).LogicalType == 7
            && !ReferenceEquals(clonedPhi.GetOperandUse(0), phi.GetOperandUse(0)), "Clone must preserve source/ABI hints with independent real Uses.");
        map[source] = clone; map[other] = clone; map[function.Arguments[0]] = LlvmConstantFP.Get(number, 5);
        foreach (var instruction in clone.Instructions) LlvmValueMapper.RemapInstruction(instruction, map);
        Program.Require(ReferenceEquals(clonedPhi.GetIncomingBlock(0), clone) && clonedPhi.GetIncomingValue(0) is LlvmConstantFP
            && clonedPhi.GetOperandUse(0).LogicalType == 7, "PHI block/value remapping must not discard the operand ABI view.");
        Program.Require(ReferenceEquals(((LlvmReturnInstruction)clone.LastInstruction!).ReturnValue, clonedPhi), "Cloned intra-block SSA operands must refer to their clone.");
        var first = LlvmFunction.Create(LlvmFunctionType.Get(number, []), LlvmLinkage.ExternalLinkage, "first", module);
        var replacement = LlvmFunction.Create(first.FunctionType, LlvmLinkage.ExternalLinkage, "replacement", module);
        var structure = LlvmStructType.Get(context, [first.Type]);
        var aggregate = LlvmConstantStruct.Get(structure, [first]);
        var unchanged = LlvmValueMapper.MapValue(aggregate, new Dictionary<LlvmValue, LlvmValue>());
        var changed = LlvmValueMapper.MapValue(aggregate, new Dictionary<LlvmValue, LlvmValue> { [first] = replacement });
        Program.Require(ReferenceEquals(unchanged, aggregate) && ReferenceEquals(changed, LlvmConstantStruct.Get(structure, [replacement]))
            && ReferenceEquals(aggregate.GetOperand(0), first), "Mapped constants must use canonical reconstruction without mutating their source.");
        Program.Require(LlvmValueMapper.MapValue(function.Arguments[0], new Dictionary<LlvmValue, LlvmValue>()) is null
            && LlvmValueMapper.MapValue(first, new Dictionary<LlvmValue, LlvmValue>(), LlvmRemapFlags.NullMapMissingGlobalValues) is null,
            "Missing locals and NullMapMissingGlobalValues must retain their source return contracts.");
    }

    private static void OperationIdentity()
    {
        LlvmContext context = new();
        var integer = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer, integer]), LlvmLinkage.InternalLinkage);
        var left = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], function.Arguments[1]) { HasNoSignedWrap = true };
        var right = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[1], function.Arguments[0]);
        Program.Require(left.IsSameOperationAs(right) && !left.IsIdenticalTo(right), "Operation identity ignores operand identity, exact identity does not.");
        var copy = (LlvmBinaryOperator)left.Clone(); copy.HasNoSignedWrap = false;
        Program.Require(left.IsIdenticalToWhenDefined(copy) && !left.IsIdenticalTo(copy), "Exact identity includes the represented optional poison flags.");
        var type = LlvmFunctionType.Get(integer, []);
        var callee = LlvmFunction.Create(type, LlvmLinkage.ExternalLinkage);
        var a = new LlvmCallInstruction(type, callee, []);
        var b = new LlvmCallInstruction(type, callee, []) { NoMerge = true };
        Program.Require(!a.IsIdenticalToWhenDefined(b, true) && !a.TryIntersectAttributes(b), "IntersectPreserve attributes cannot be silently dropped.");
        a.NoMerge = true;
        Program.Require(a.IsIdenticalToWhenDefined(b, true) && a.TryIntersectAttributes(b), "Matching source preserve attributes must intersect successfully.");
    }

    private static void OperandReplacement()
    {
        LlvmContext context = new();
        var integer = LlvmType.GetInt32Ty(context);
        var module = new LlvmModule("replace-operands", context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, []), LlvmLinkage.InternalLinkage, "entry", module);
        LlvmBasicBlock block = new(context, "entry", function);
        var one = LlvmConstantInt.Get(integer, 1);
        var structure = LlvmStructType.Get(context, [integer]);
        var allocation = new LlvmAllocaInstruction(structure, one); block.Append(allocation);
        var gep = new LlvmGetElementPtrInstruction(structure, allocation, [one, LlvmConstantInt.Get(integer, 0)]);
        Program.Require(!LlvmLocal.CanReplaceOperandWithVariable(allocation, 0)
            && LlvmLocal.CanReplaceOperandWithVariable(gep, 1) && !LlvmLocal.CanReplaceOperandWithVariable(gep, 2),
            "Static allocation size and struct indices retain their source replacement restrictions.");
        var call = LlvmIntrinsic.CreateBinary(LlvmIntrinsicKind.UMin, one, one, module);
        Program.Require(LlvmLocal.CanReplaceOperandWithVariable(call, 0) && !LlvmLocal.CanReplaceOperandWithVariable(call, call.OperandCount - 1),
            "Ordinary min/max arguments may vary, but an intrinsic callee cannot become a variable.");
    }

    private static async Task ExecuteCloneAsync()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("0", "cloning-journey.msync"));
        int number = input.Types.ToList().FindIndex(type => type.Kind == TypeKind.Number && type.Pointee < 0);
        var type = LlvmFunctionType.Get(LlvmType.GetDoubleTy(input.Module.Context), [LlvmType.GetDoubleTy(input.Module.Context)]);
        var original = LlvmFunction.Create(type, LlvmLinkage.InternalLinkage, "original-calc", input.Module);
        var cloned = LlvmFunction.Create(type, LlvmLinkage.InternalLinkage, "cloned-calc", input.Module);
        original.Arguments[0].LogicalType = cloned.Arguments[0].LogicalType = number;
        LlvmBasicBlock body = new(input.Module.Context, "entry", original);
        var sum = new LlvmBinaryOperator(LlvmOpcode.FAdd, original.Arguments[0], LlvmConstantFP.Get(type.ReturnType, 1)) { LogicalType = number };
        body.Append(sum); body.Append(new LlvmReturnInstruction(input.Module.Context, sum));
        Dictionary<LlvmValue, LlvmValue> map = new() { [original.Arguments[0]] = cloned.Arguments[0] };
        var info = new LlvmClonedCodeInfo();
        var copy = LlvmCloning.CloneBasicBlock(body, map, ".copy", cloned, info);
        map[body] = copy;
        foreach (var instruction in copy.Instructions) LlvmValueMapper.RemapInstruction(instruction, map);
        var functions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value);
        functions.Add(original, new(0, number)); functions.Add(cloned, new(0, number));
        var program = LlvmNativeLowerer.Lower(input with { SourceFunctions = functions });
        using MemoryStream artifact = new(); NativeArtifact.Write(program, artifact); artifact.Position = 0;
        foreach (var executable in new[] { program, NativeArtifact.Read(artifact) })
        {
            int index = Enumerable.Range(0, executable.Functions.Count).Single(i => executable.Functions[i].Name == "cloned-calc");
            await using NativeInterpreter interpreter = new(executable);
            var result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)), [NativeValue.FromNumber(41)]);
            Program.Require(result.AsNumber() == 42, "A cloned/remapped SSA graph must execute through the Native target and detached Native2 artifact.");
        }
    }
}
