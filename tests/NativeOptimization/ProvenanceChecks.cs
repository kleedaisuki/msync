using Msync.Core;
using Msync.Core.Native;

internal static class ProvenanceChecks
{
    internal static int Run()
    {
        NativeCodeType[] pointers = [new(TypeKind.Number), new(TypeKind.Bool),
            new(TypeKind.Never, Pointee: 0), new(TypeKind.Never, Pointee: 1)];
        if (NativeTypeRelations.StorageAccepts(pointers, 2, 3) || NativeTypeRelations.StorageAccepts(pointers, 3, 2))
            throw new InvalidOperationException("Typed pointer storage must preserve its pointee invariant");
        int checks = 2;
        foreach (bool exposeOuter in new[] { false, true })
        foreach (bool storeAfterCall in new[] { false, true })
        {
            NativeProgram program = Example(exposeOuter, storeAfterCall);
            NativeArtifact.Validate(program);
            NativeAddressAnalysis facts = new(program, program.Functions[0]);
            IReadOnlySet<int>? proven = facts.ProvenLayoutsAt(program.Functions[0].Instructions.Count - 1, 5);
            if (exposeOuter ? proven is not null : proven is not { Count: 1 } || !proven.Contains(1))
                throw new InvalidOperationException($"Stored referent evidence mishandled exposed={exposeOuter}, delayedStore={storeAfterCall}");
            checks++;
        }
        return checks;
    }

    private static NativeProgram Example(bool exposeOuter, bool storeAfterCall)
    {
        NativeCodeType[] types = [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Scope, Layouts: [1]),
            new(TypeKind.Number), new(TypeKind.Never, Pointee: 1), new(TypeKind.Function), new(TypeKind.Null),
            new(TypeKind.Scope, Layouts: [2])];
        NativeInstruction store = new(NativeOp.Store, A: 2, B: 0, C: 0, Type: 1);
        List<NativeInstruction> code = [new(NativeOp.Allocate, 0, 1), new(NativeOp.Allocate, 1, 0),
            new(NativeOp.AddressOffset, 2, 1, 0, 0), new(NativeOp.Allocate, 6, 0),
            new(NativeOp.MakeFunction, 3, 1, Inputs: [])];
        if (!storeAfterCall) code.Add(store);
        code.Add(new(NativeOp.Call, 4, 3, 1, Inputs: [exposeOuter ? 1 : 6]));
        if (storeAfterCall) code.Add(store);
        code.Add(new(NativeOp.Load, 5, 2, Type: 1));
        code.Add(new(NativeOp.Return, A: 5));
        NativeFunctionCode main = new("main", [new(1), new(0), new(3), new(4), new(5), new(1), new(0)],
            code, new([], 1, []));
        NativeFunctionCode effect = new("effect", [new(0, NativeSlotRole.Parameter), new(3), new(6), new(5)],
            [new(NativeOp.AddressOffset, 1, 0, 0, 0), new(NativeOp.Allocate, 2, 2),
                new(NativeOp.Store, A: 1, B: 2, C: 0, Type: 1), new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)],
            new([0], 5, []), ParameterCount: 1);
        return new([new(NativeKind.Null)], [main, effect], types,
            [new([new("value", 1)]), new([new("number", 2)]), new([new("different", 5)])]);
    }
}
