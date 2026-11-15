using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class RauwAbiChecks
{
    internal static void Run()
    {
        LlvmContext context = new();
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        LlvmStructType descriptor = LlvmStructType.Create(context, [pointer, pointer, pointer], "callable.rauw");
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(descriptor,
            [LlvmType.GetInt1Ty(context), descriptor, descriptor]), LlvmLinkage.InternalLinkage);
        LlvmBasicBlock incoming = new(context);
        var select = new LlvmSelectInstruction(function.Arguments[0], function.Arguments[1], function.Arguments[2]);
        LlvmPhiInstruction phi = new(descriptor, 2) { LogicalType = 11 };
        phi.AddIncoming(select, incoming);
        phi.AddIncoming(select, incoming);
        var result = new LlvmReturnInstruction(context, phi);
        result.GetOperandUse(0).LogicalType = 22;
        Program.Require(!select.UseEmpty, "Exercise the actual speculation path: the replacement already has PHI uses.");
        phi.ReplaceAllUsesWith(select);
        Program.Require(select.LogicalType == 11 && ReferenceEquals(result.ReturnValue, select),
            "A replacement SSA definition must inherit the replaced aggregate's otherwise missing ABI hint.");
        Program.Require(result.GetOperandUse(0).LogicalType == 22,
            "An explicit consumer ABI must remain authoritative over the new definition hint.");

        var existing = new LlvmSelectInstruction(function.Arguments[0], function.Arguments[1], function.Arguments[2])
            { LogicalType = 33 };
        select.ReplaceAllUsesWith(existing);
        Program.Require(existing.LogicalType == 33 && result.GetOperandUse(0).LogicalType == 22,
            "RAUW must not overwrite an established definition ABI or a consumer view.");

        foreach (LlvmConstant constant in new LlvmConstant[]
        {
            LlvmConstantPointerNull.Get(pointer), LlvmUndefValue.Get(pointer), LlvmPoisonValue.Get(pointer),
            LlvmConstantAggregateZero.Get(descriptor), LlvmUndefValue.Get(descriptor)
        })
        {
            LlvmFunction holder = LlvmFunction.Create(LlvmFunctionType.Get(constant.Type,
                [constant.Type, constant.Type]), LlvmLinkage.InternalLinkage);
            holder.Arguments[0].LogicalType = 41;
            holder.Arguments[1].LogicalType = 42;
            var first = new LlvmReturnInstruction(context, holder.Arguments[0]);
            var second = new LlvmReturnInstruction(context, holder.Arguments[1]);
            holder.Arguments[0].ReplaceAllUsesWith(constant);
            holder.Arguments[1].ReplaceAllUsesWith(constant);
            Program.Require(constant.LogicalType == -1
                && ReferenceEquals(first.ReturnValue, second.ReturnValue)
                && first.GetOperandUse(0).LogicalType == 41 && second.GetOperandUse(0).LogicalType == 42,
                "Context-unique constants must keep different ABI expectations on uses, never on the constant.");
        }
    }

    internal static async Task<int> RunSourceAsync(string sourceFile)
    {
        string source = File.ReadAllText(sourceFile);
        int executions = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram program = Compiler.Compile(source, sourceFile, optimization);
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            bytes.Position = 0;
            foreach (NativeProgram executable in new[] { program, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(executable);
                double result = (await interpreter.ExecuteAsync()).AsNumber();
                Program.Require(result == 56, $"Dynamic callable select O{optimization}: expected 56, got {result}.");
                ++executions;
            }
        }
        return executions;
    }
}
