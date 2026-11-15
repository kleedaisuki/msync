using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime.Native;

internal static class SinkOperandAbiChecks
{
    internal static async Task<int> RunSourceAsync(string sourceFile)
    {
        int executions = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram program = Compiler.Compile(File.ReadAllText(sourceFile), sourceFile, optimization);
            using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
            foreach (NativeProgram executable in new[] { program, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(executable);
                Program.Require((await interpreter.ExecuteAsync()).AsNumber() == 79,
                    $"Captured variable selection O{optimization} must preserve both location loads.");
                ++executions;
            }
        }
        return executions;
    }

    internal static async Task<int> RunAsync()
    {
        int executions = 0;
        foreach (bool chooseLeft in new[] { false, true })
        foreach (bool useAnnotation in new[] { false, true })
        {
            var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "sink-operand-abi.msync"));
            var types = input.Types.ToList();
            int number = types.FindIndex(type => type.Kind == TypeKind.Number && type.Pointee < 0);
            int pointer = types.Count; types.Add(new(TypeKind.Never, Pointee: number));
            int operandView = pointer;
            if (useAnnotation) { operandView = types.Count; types.Add(new(TypeKind.Never, Pointee: number)); }
            var context = input.Module.Context;
            var scalar = LlvmType.GetDoubleTy(context);
            var module = new LlvmModule("sink-operand-abi", context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(scalar, []), LlvmLinkage.InternalLinkage,
                "sink-operand-abi", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var left = new LlvmBasicBlock(context, "left", function);
            var right = new LlvmBasicBlock(context, "right", function);
            var join = new LlvmBasicBlock(context, "join", function);
            var one = LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1);
            var first = new LlvmAllocaInstruction(scalar, one) { LogicalType = pointer, AllocatedLogicalType = number };
            var second = new LlvmAllocaInstruction(scalar, one) { LogicalType = pointer, AllocatedLogicalType = number };
            entry.Append(first); entry.Append(second);
            entry.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(context, new LlvmAPFloat(7)), first));
            entry.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(context, new LlvmAPFloat(9)), second));
            entry.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetBool(context, chooseLeft), left, right));
            var firstLoad = new LlvmLoadInstruction(scalar, first) { LogicalType = number };
            var secondLoad = new LlvmLoadInstruction(scalar, second) { LogicalType = number };
            if (useAnnotation)
            {
                firstLoad.GetOperandUse(0).LogicalType = operandView;
                secondLoad.GetOperandUse(0).LogicalType = operandView;
            }
            left.Append(firstLoad); left.Append(new LlvmUncondBrInstruction(join));
            right.Append(secondLoad); right.Append(new LlvmUncondBrInstruction(join));
            var result = new LlvmPhiInstruction(scalar, 2) { LogicalType = number };
            result.AddIncoming(firstLoad, left); result.AddIncoming(secondLoad, right);
            join.Append(result); join.Append(new LlvmReturnInstruction(context, result));
            Program.Require(LlvmSimplifyCfg.SimplifyCFG(join, new(module.DataLayout),
                options: new() { SinkCommonInsts = true, SpeculateBlocks = false }),
                "The real SimplifyCFG sinker must transform the join block.");
            var pointerPhi = join.Instructions.OfType<LlvmPhiInstruction>().Single(phi => phi.Type.IsPointerTy());
            Program.Require(pointerPhi.LogicalType == operandView,
                "A fresh sink operand PHI must inherit the originating Use ABI, or its nonconstant definition hint.");
            for (int index = 0; index < pointerPhi.IncomingCount; ++index)
                Program.Require(pointerPhi.GetOperandUse(index).LogicalType == operandView,
                    "Each new PHI operand must retain its originating operand ABI.");
            Program.Require(join.Instructions.OfType<LlvmLoadInstruction>().Count() == 1,
                "The component must exercise two actual loads sunk into one, not manufacture a metadata-only PHI.");
            var graph = input with
            {
                Module = module, Entry = function, Types = types,
                SourceFunctions = new Dictionary<LlvmFunction, LlvmSourceFunctionInfo> { [function] = new(0, number) }
            };
            NativeProgram program = LlvmNativeLowerer.Lower(graph);
            using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
            foreach (NativeProgram executable in new[] { program, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(executable);
                Program.Require((await interpreter.ExecuteAsync()).AsNumber() == (chooseLeft ? 7 : 9),
                    "Sunk pointer PHI must execute the chosen location through both direct and detached Native2 target paths.");
                ++executions;
            }
        }
        return executions;
    }
}
