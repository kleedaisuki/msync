using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class ConstantAbiUses
{
    internal static void Run()
    {
        Replacement();
        Materialization();
    }

    internal static async Task RunFoldedFunctionsAsync()
    {
        (string Name, string Source, double Result)[] cases =
        [
            ("source", "fn add(left: Number, right: Number) -> Number { left + right }; add(3, 4)", 7),
            ("native-prefix", "$std::math::sqrt(9)", 3)
        ];
        foreach (var test in cases)
        {
            LlvmHirLoweringResult input = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, "folded-" + test.Name + ".msync"));
            int folds = 0;
            foreach (var function in input.Module.Functions)
                foreach (var block in function.BasicBlocks)
                    foreach (var instruction in block.Instructions.ToArray())
                    {
                        if (instruction is not LlvmInsertValueInstruction ||
                            LlvmConstantFolding.ConstantFoldInstruction(instruction, input.Module.DataLayout) is not { } constant) continue;
                        instruction.ReplaceAllUsesWith(constant);
                        instruction.EraseFromParent();
                        ++folds;
                    }
            Program.Require(folds > 0, test.Name + ": source descriptor construction must actually fold.");
            NativeProgram program = LlvmNativeLowerer.Lower(input);
            string work = Path.GetFullPath(Path.Combine(".temp", "llvm-ir", "folded-functions", test.Name));
            Directory.CreateDirectory(work);
            await using NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: work));
            NativeValue result = await interpreter.ExecuteAsync();
            Program.Require(result.AsNumber() == test.Result,
                $"Folded {test.Name} descriptor: expected {test.Result}, actual {result.AsNumber()}.");
        }
    }

    private static void Replacement()
    {
        LlvmContext context = new();
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(pointer, [pointer, pointer]),
            LlvmLinkage.InternalLinkage);
        function.Arguments[0].LogicalType = 11;
        function.Arguments[1].LogicalType = 22;
        LlvmReturnInstruction first = new(context, function.Arguments[0]);
        LlvmReturnInstruction second = new(context, function.Arguments[1]);
        LlvmConstantPointerNull shared = LlvmConstantPointerNull.Get(pointer);
        function.Arguments[0].ReplaceAllUsesWith(shared);
        function.Arguments[1].ReplaceAllUsesWith(shared);
        Program.Require(ReferenceEquals(first.ReturnValue, second.ReturnValue) && shared.LogicalType == -1
            && first.GetOperandUse(0).LogicalType == 11 && second.GetOperandUse(0).LogicalType == 22,
            "RAUW must retain each operand ABI expectation without changing a shared constant.");
        Program.Require(first.Clone().GetOperandUse(0).LogicalType == 11,
            "Instruction cloning must preserve per-use target ABI metadata.");

        LlvmBasicBlock block = new(context);
        LlvmPhiInstruction phi = new(pointer, 1);
        phi.AddIncoming(shared, block);
        phi.GetOperandUse(0).LogicalType = 11;
        phi.AddIncoming(shared, block);
        phi.GetOperandUse(1).LogicalType = 22;
        Program.Require(phi.GetOperandUse(0).LogicalType == 11,
            "PHI operand growth must retain the existing use annotation.");
        phi.RemoveIncomingValue(0);
        Program.Require(phi.GetOperandUse(0).LogicalType == 22,
            "Moving the last PHI incoming edge must move its ABI annotation as well.");
    }

    private static void Materialization()
    {
        LlvmHirLoweringResult input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "constant-abi.msync"));
        LlvmContext context = input.Module.Context;
        List<NativeCodeType> types = input.Types.ToList();
        int number = types.FindIndex(type => type.Kind == TypeKind.Number && type.Pointee < 0);
        int nullable = types.Count;
        types.Add(new(TypeKind.Number | TypeKind.Null));
        LlvmModule module = new("constant-abi", context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetDoubleTy(context), []),
            LlvmLinkage.InternalLinkage, "constant-abi", module);
        LlvmConstantFP shared = LlvmConstantFP.Get(context, new LlvmAPFloat(7));
        LlvmBasicBlock first = new(context, "number", function), second = new(context, "nullable", function);
        LlvmReturnInstruction mono = new(context, shared), union = new(context, shared);
        mono.GetOperandUse(0).LogicalType = number;
        union.GetOperandUse(0).LogicalType = nullable;
        first.Append(mono);
        second.Append(union);
        NativeProgram program = LlvmNativeLowerer.Lower(input with
        {
            Module = module, Entry = function, Types = types,
            SourceFunctions = new Dictionary<LlvmFunction, LlvmSourceFunctionInfo>
            {
                [function] = new(0, nullable)
            }
        });
        NativeFunctionCode body = program.Functions[program.EntryPoint];
        int[] materialized = body.Instructions.Where(instruction => instruction.Op == NativeOp.Constant)
            .Select(instruction => body.Slots[instruction.Result].Type).ToArray();
        Program.Require(materialized.Length == 2 && materialized.Contains(number) && materialized.Contains(nullable),
            "A canonical constant used with two native ABI representations needs two contextual slots.");
    }
}
